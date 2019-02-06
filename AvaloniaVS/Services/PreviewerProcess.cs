﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;
using Microsoft.VisualStudio.Shell;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Task = System.Threading.Tasks.Task;

namespace AvaloniaVS.Services
{
    /// <summary>
    /// Manages running a XAML previewer process.
    /// </summary>
    public class PreviewerProcess : IDisposable, ILogEventEnricher
    {
        private readonly ILogger _log;
        private string _executablePath;
        private Process _process;
        private IAvaloniaRemoteTransportConnection _connection;
        private IDisposable _listener;
        private WriteableBitmap _bitmap;
        private ExceptionDetails _error;

        /// <summary>
        /// Initializes a new instance of the <see cref="PreviewerProcess"/> class.
        /// </summary>
        public PreviewerProcess()
        {
            _log = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Destructure.ToMaximumStringLength(32)
                .Enrich.With(this)
                .WriteTo.Logger(Log.Logger)
                .CreateLogger();
        }

        /// <summary>
        /// Gets the current preview as a <see cref="BitmapSource"/>.
        /// </summary>
        public BitmapSource Bitmap => _bitmap;

        /// <summary>
        /// Gets the current error state as returned from the previewer process.
        /// </summary>
        public ExceptionDetails Error
        {
            get => _error;
            private set
            {
                if (!Equals(_error, value))
                {
                    _error = value;
                    ErrorChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether the previewer process is currently running.
        /// </summary>
        public bool IsRunning => _process != null && !_process.HasExited;

        /// <summary>
        /// Gets a value indicating whether the previewer process is ready to receive messages.
        /// </summary>
        public bool IsReady => IsRunning && _connection != null;

        /// <summary>
        /// Raised when the <see cref="Error"/> state changes.
        /// </summary>
        public event EventHandler ErrorChanged;

        /// <summary>
        /// Raised when a new frame is available in <see cref="Bitmap"/>.
        /// </summary>
        public event EventHandler FrameReceived;
        
        /// <summary>
        /// Starts the previewer process.
        /// </summary>
        /// <param name="executablePath">The path to the executable to preview.</param>
        /// <returns>A task tracking the startup operation.</returns>
        public async Task StartAsync(string executablePath)
        {
            _log.Verbose("Started PreviewerProcess.StartAsync()");

            if (_listener != null)
            {
                throw new InvalidOperationException("Previewer process already started.");
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException(
                    "Executable path may not be null or an empty string.",
                    nameof(executablePath));
            }

            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    $"Could not find executable '{executablePath}'. " + 
                    "Please build your project to enable previewing and intellisense.");
            }

            _executablePath = executablePath;

            var port = FreeTcpPort();
            var tcs = new TaskCompletionSource<object>();

            _listener = new BsonTcpTransport().Listen(
                IPAddress.Loopback,
                port,
#pragma warning disable VSTHRD101
                async t =>
                {
                    try
                    {
                        await ConnectionInitializedAsync(t);
                    } catch (Exception ex)
                    {
                        _log.Error(ex, "Error initializing connection");
                    }
                    finally
                    {
                        tcs.SetResult(null);
                    }
                });
#pragma warning restore VSTHRD101

            var executableDir = Path.GetDirectoryName(_executablePath);
            var extensionDir = Path.GetDirectoryName(GetType().Assembly.Location);
            var targetName = Path.GetFileNameWithoutExtension(_executablePath);
            var runtimeConfigPath = Path.Combine(executableDir, targetName + ".runtimeconfig.json");
            var depsPath = Path.Combine(executableDir, targetName + ".deps.json");
            var hostAppPath = Path.Combine(extensionDir, "Avalonia.Designer.HostApp.dll");

            EnsureExists(runtimeConfigPath);
            EnsureExists(depsPath);
            EnsureExists(depsPath);

            var args = $@"exec --runtimeconfig ""{runtimeConfigPath}"" --depsfile ""{depsPath}"" ""{hostAppPath}"" --transport tcp-bson://127.0.0.1:{port}/ ""{_executablePath}""";

            var processInfo = new ProcessStartInfo
            {
                Arguments = args,
                CreateNoWindow = true,
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            _log.Information("Starting previewer process for '{ExecutablePath}'", _executablePath);
            _log.Debug("> dotnet.exe {Args}", args);

            _process = Process.Start(processInfo);
            _process.OutputDataReceived += ProcessOutputReceived;
            _process.ErrorDataReceived += ProcessErrorReceived;
            _process.Exited += ProcessExited;
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            if (!_process.HasExited)
            {
                _log.Information("Started previewer process for '{ExecutablePath}'", _executablePath);
                await tcs.Task;
            }
            else
            {
                throw new ApplicationException($"The previewer process exited unexpectedly with code {_process.ExitCode}.");
            }

            _log.Verbose("Finished PreviewerProcess.StartAsync()");
        }

        /// <summary>
        /// Stops the previewer process.
        /// </summary>
        public void Stop()
        {
            _log.Verbose("Started PreviewerProcess.Stop()");
            _log.Information("Stopping previewer process");

            _listener?.Dispose();
            _listener = null;

            if (_connection != null)
            {
                _connection.OnMessage -= ConnectionMessageReceived;
                _connection.OnException -= ConnectionExceptionReceived;
                _connection.Dispose();
                _connection = null;
            }

            if (_process != null && !_process.HasExited)
            {
                _log.Debug("Killing previewer process");

                try
                {
                    // Kill the process. Do not set _process to null here, wait for ProcessExited to be called.
                    _process.Kill();
                }
                catch (InvalidOperationException ex)
                {
                    _log.Debug(ex, "Failed to kill previewer process");
                }
            }

            _executablePath = null;

            _log.Verbose("Finished PreviewerProcess.Stop()");
        }

        /// <summary>
        /// Updates the XAML to be previewed.
        /// </summary>
        /// <param name="xaml">The XAML.</param>
        /// <returns>A task tracking the operation.</returns>
        public async Task UpdateXamlAsync(string xaml)
        {
            if (_process == null)
            {
                throw new InvalidOperationException("Process not started.");
            }

            if (_connection == null)
            {
                throw new InvalidOperationException("Process not finished initializing.");
            }

            await SendAsync(new UpdateXamlMessage
            {
                Xaml = xaml,
            });
        }

        /// <summary>
        /// Stops the process and disposes of all resources.
        /// </summary>
        public void Dispose() => Stop();

        void ILogEventEnricher.Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (_process?.HasExited != true)
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("Pid", _process?.Id ?? 0));
            }
        }

        private async Task ConnectionInitializedAsync(IAvaloniaRemoteTransportConnection connection)
        {
            _log.Verbose("Started PreviewerProcess.ConnectionInitializedAsync()");
            _log.Information("Connection initialized");

            _connection = connection;
            _connection.OnException += ConnectionExceptionReceived;
            _connection.OnMessage += ConnectionMessageReceived;

            await SendAsync(new UpdateXamlMessage
            {
                AssemblyPath = _executablePath,
            });

            await SendAsync(new ClientSupportedPixelFormatsMessage
            {
                Formats = new[]
                {
                    Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888,
                    Avalonia.Remote.Protocol.Viewport.PixelFormat.Rgba8888,
                }
            });

            await SendAsync(new ClientRenderInfoMessage
            {
                DpiX = 96,
                DpiY = 96,
            });

            _log.Verbose("Finished PreviewerProcess.ConnectionInitializedAsync()");
        }

        private async Task SendAsync(object message)
        {
            _log.Debug("=> Sending {@Message}", message);
            await _connection.Send(message);
            _log.Debug("=> Sent {@Message}", message);
        }

        private async Task OnMessageAsync(object message)
        {
            _log.Verbose("Started PreviewerProcess.OnMessageAsync()");
            _log.Debug("<= {@Message}", message);

            if (message is FrameMessage frame)
            {
                if (Error == null)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                    {
                        _bitmap = new WriteableBitmap(
                            frame.Width,
                            frame.Height,
                            96,
                            96,
                            ToWpf(frame.Format),
                            null);
                    }

                    _bitmap.WritePixels(
                        new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight),
                        frame.Data,
                        frame.Stride,
                        0);

                    FrameReceived?.Invoke(this, EventArgs.Empty);
                }

                await SendAsync(new FrameReceivedMessage
                {
                    SequenceId = frame.SequenceId
                });
            }
            else if (message is UpdateXamlResultMessage update)
            {
                var error = update.Exception;

                if (error == null && !string.IsNullOrWhiteSpace(update.Error))
                {
                    error = new ExceptionDetails { Message = update.Error };
                }

                Error = error;
            }

            _log.Verbose("Finished PreviewerProcess.OnMessageAsync()");
        }

        private void ConnectionMessageReceived(IAvaloniaRemoteTransportConnection connection, object message)
        {
            OnMessageAsync(message).FireAndForget();
        }

        private void ConnectionExceptionReceived(IAvaloniaRemoteTransportConnection connection, Exception ex)
        {
            _log.Error(ex, "Connection error");
        }

        private void ProcessOutputReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _log.Debug("<= {Data}", e.Data);
            }
        }

        private void ProcessErrorReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _log.Error("<= {Data}", e.Data);
            }
        }

        private void ProcessExited(object sender, EventArgs e)
        {
            _log.Information("Process exited");
            _process = null;
        }

        private static void EnsureExists(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Could not find '{path}'.");
            }
        }

        private static int FreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static bool Equals(ExceptionDetails a, ExceptionDetails b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            return a?.ExceptionType == b?.ExceptionType &&
                a?.Message == b?.Message &&
                a?.LineNumber == b?.LineNumber &&
                a?.LinePosition == b?.LinePosition;
        }

        private System.Windows.Media.PixelFormat ToWpf(Avalonia.Remote.Protocol.Viewport.PixelFormat format)
        {
            switch (format)
            {
                case Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888:
                    return PixelFormats.Bgra32;
                case Avalonia.Remote.Protocol.Viewport.PixelFormat.Rgb565:
                    return PixelFormats.Bgr565;
                case Avalonia.Remote.Protocol.Viewport.PixelFormat.Rgba8888:
                    return PixelFormats.Pbgra32;
                default:
                    throw new NotSupportedException("Unsupported pixel format.");
            }
        }
    }
}
