﻿using System;
using System.Collections.Generic;
using Avalonia.Remote.Protocol.Designer;
using AvaloniaVS.Services;
using AvaloniaVS.Views;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Tagging;

namespace AvaloniaVS.IntelliSense
{
    internal class XamlErrorTagger : ITagger<IErrorTag>, ITableDataSource, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly ITextStructureNavigator _navigator;
        private DesignerPane _pane;
        private PreviewerProcess _process;
        private ExceptionDetails _error;
        private ITableDataSink _sink;

        public XamlErrorTagger(
            ITableManagerProvider tableManagerProvider,
            ITextBuffer buffer,
            ITextStructureNavigator navigator,
            DesignerPane pane)
        {
            _buffer = buffer;
            _navigator = navigator;
            _pane = pane;

            if (pane.Process != null)
            {
                _process = pane.Process;
                _process.ErrorChanged += HandleErrorChanged;
            }
            else
            {
                pane.Initialized += PaneInitialized;
            }

            // Register ourselves with the error list.
            var tableManager = tableManagerProvider.GetTableManager(StandardTables.ErrorsTable);
            tableManager.AddSource(this,
                StandardTableColumnDefinitions.Column,
                StandardTableColumnDefinitions.DocumentName,
                StandardTableColumnDefinitions.ErrorSeverity,
                StandardTableColumnDefinitions.Line,
                StandardTableColumnDefinitions.Text);
        }

        string ITableDataSource.SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;
        string ITableDataSource.Identifier => "Avalonia XAML designer errors";
        string ITableDataSource.DisplayName => "Avalonia XAML";

        public event EventHandler Disposed;
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public void Dispose()
        {
            _sink?.RemoveAllEntries();
            _pane.Initialized -= PaneInitialized;

            if (_process != null)
            {
                _process.ErrorChanged -= HandleErrorChanged;
            }

            Disposed?.Invoke(this, EventArgs.Empty);
        }

        public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (_error?.LineNumber != null)
            {
                var line = _buffer.CurrentSnapshot.GetLineFromLineNumber(_error.LineNumber.Value - 1);
                var start = line.Start + ((_error?.LinePosition ?? 1) - 1);
                var startSpan = new SnapshotSpan(start, start + 1);
                var span = _navigator.GetSpanOfFirstChild(startSpan);
                var tag = new ErrorTag(PredefinedErrorTypeNames.CompilerError, _error.Message);
                var tableErrors = new[] { new XamlErrorTableEntry(_error) };

                _sink?.AddEntries(tableErrors, true);

                return new[] { new TagSpan<IErrorTag>(span, tag) };
            }

            return Array.Empty<ITagSpan<IErrorTag>>();
        }

        IDisposable ITableDataSource.Subscribe(ITableDataSink sink)
        {
            _sink = sink;
            
            if (_error != null)
            {
                sink.AddEntries(new[] { new XamlErrorTableEntry(_error) });
            }

            return null;
        }

        private void HandleErrorChanged(object sender, EventArgs e)
        {
            RaiseTagsChanged(_error);
            _error = _process.Error;
            RaiseTagsChanged(_error);
        }

        private void RaiseTagsChanged(ExceptionDetails error)
        {
            if (error?.LineNumber != null &&
                TagsChanged != null &&
                error.LineNumber.Value < _buffer.CurrentSnapshot.LineCount)
            {
                var line = _buffer.CurrentSnapshot.GetLineFromLineNumber(error.LineNumber.Value - 1);
                TagsChanged(this, new SnapshotSpanEventArgs(line.Extent));
            }
        }

        private void PaneInitialized(object sender, EventArgs e)
        {
            _process = _pane.Process;
            _process.ErrorChanged += HandleErrorChanged;
            RaiseTagsChanged(_process.Error);
            _pane.Initialized -= PaneInitialized;
        }
    }
}
