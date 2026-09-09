using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using XpdfReader.WinUI.Services;

namespace XpdfReader.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly SemaphoreSlim _documentGate = new(1, 1);
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resizeTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _viewportTimer;
    private readonly List<PdfSession> _sessions = new();
    private readonly ObservableCollection<ThumbnailItem> _thumbnailItems = new();
    private readonly Dictionary<Guid, Dictionary<int, ImageSource?>> _thumbnailCache = new();
    private InputNonClientPointerSource? _titleBarInputSource;
    private CancellationTokenSource _renderCancellation = new();
    private CancellationTokenSource _searchCancellation = new();
    private CancellationTokenSource _textCancellation = new();
    private CancellationTokenSource _copyCancellation = new();
    private CancellationTokenSource _layoutCancellation = new();
    private PdfSession? _activeSession;
    private PdfDocument? _document;
    private readonly Dictionary<int, PageSurface> _pageSurfaces = new();
    private PageSize[]? _pageSizes;
    private PageLayout[]? _pageLayouts;
    private string? _initialPath;
    private string _documentName = string.Empty;
    private string _lastQuery = string.Empty;
    private string _searchMessage = string.Empty;
    private int _pageCount;
    private int _page = 1;
    private int _rotation;
    private int _zoomMode;
    private int _pageSizesRotation = -1;
    private bool _sizesComplete;
    private int _layoutVersion;
    private int _openVersion;
    private int _renderVersion;
    private int _textVersion;
    private int _searchVersion;
    private int _lastMatchPage;
    private int _lastMatchOffset = -1;
    private bool _opening;
    private bool _rendering;
    private bool _searching;
    private bool _layingOut;
    private bool _loadingOutline;
    private bool _closed;
    private bool _updatingUi;
    private bool _pickerOpen;
    private bool _titleBarRegionsPending;
    private bool _sidebarVisible;
    private bool _sidebarOutlineMode = true;
    private ReaderDisplayMode _displayMode;
    private double _activeZoom = 1;

    private const double PageMargin = 16;
    private const double PageGap = 20;
    private const int MaxResidentPages = 6;
    private const int PrefetchPages = 2;
    // Reuse a cached bitmap when it still has at least this fraction of the
    // resolution the current layout needs, so small fit-zoom changes (resizing,
    // opening/closing the sidebar) do not re-rasterize every visible page.
    private const double ReuseResolutionTolerance = 0.45;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarRow);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        try
        {
            _titleBarInputSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        }
        catch (Exception)
        {
            // Custom caption hit testing is unavailable on older Windows builds.
            _titleBarInputSource = null;
        }
        TitleBarRow.SizeChanged += (_, _) => QueueTitleBarRegionsUpdate();
        TabStrip.LayoutUpdated += (_, _) => QueueTitleBarRegionsUpdate();

        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(180);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += async (_, _) => await RefreshLayoutAsync(false);
        _viewportTimer = DispatcherQueue.CreateTimer();
        _viewportTimer.Interval = TimeSpan.FromMilliseconds(90);
        _viewportTimer.IsRepeating = false;
        _viewportTimer.Tick += async (_, _) => await UpdateViewportAsync();
        Closed += MainWindow_Closed;
        AppWindow.Changed += (_, args) =>
        {
            UpdateTitleBarInsets();
            if (args.DidSizeChange && !_closed &&
                AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Minimized })
            {
                Windows.Graphics.SizeInt32 size = AppWindow.Size;
                if (size.Width < 520 || size.Height < 420)
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(520, size.Width), Math.Max(420, size.Height)));
                }
                UpdateTextPaneLayout();
            }
        };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 840));

        RebuildTabStrip();
        SetSidebarMode(true);
        UpdateSidebar();
        UpdateTitleBar();
        UpdateTitleBarInsets();
        QueueTitleBarRegionsUpdate();
    }

    public void QueueInitialDocument(string path)
    {
        _initialPath = path;
        if (RootGrid.IsLoaded)
        {
            _ = OpenDocumentAsync(path);
            _initialPath = null;
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialPath is string path)
        {
            _initialPath = null;
            await OpenDocumentAsync(path);
        }
    }

    // Active session state is mirrored into the fields above so the rendering
    // paths stay simple. Save/apply copy the whole snapshot on tab switches.
    private void SaveActiveSession()
    {
        if (_activeSession is null)
        {
            return;
        }
        _activeSession.Document = _document;
        _activeSession.Name = _documentName;
        _activeSession.PageCount = _pageCount;
        _activeSession.Page = _page;
        _activeSession.Rotation = _rotation;
        _activeSession.ZoomMode = _zoomMode;
        _activeSession.DisplayMode = _displayMode;
        _activeSession.PageSizes = _pageSizes;
        _activeSession.PageLayouts = _pageLayouts;
        _activeSession.PageSizesRotation = _pageSizesRotation;
        _activeSession.SizesComplete = _sizesComplete;
        _activeSession.PageText = PageTextBox.Text;
        _activeSession.LastQuery = _lastQuery;
        _activeSession.LastMatchPage = _lastMatchPage;
        _activeSession.LastMatchOffset = _lastMatchOffset;
    }

    private void ApplySession(PdfSession session)
    {
        _document = session.Document;
        _documentName = session.Name;
        _pageCount = session.PageCount;
        _page = Math.Max(1, session.Page);
        _rotation = session.Rotation;
        _zoomMode = session.ZoomMode;
        _displayMode = session.DisplayMode;
        _pageSizes = session.PageSizes;
        _pageLayouts = session.PageLayouts;
        _pageSizesRotation = session.PageSizesRotation;
        _sizesComplete = session.SizesComplete;
        _lastQuery = session.LastQuery;
        _lastMatchPage = session.LastMatchPage;
        _lastMatchOffset = session.LastMatchOffset;
        _searchMessage = string.Empty;
        PageTextBox.Text = session.PageText;
        ZoomText.Text = string.Empty;
    }

    private async Task ActivateSessionAsync(PdfSession target)
    {
        if (target is null || ReferenceEquals(target, _activeSession) || _closed)
        {
            return;
        }
        SaveActiveSession();
        _activeSession = target;
        ApplySession(target);
        CancelPendingWork();
        _opening = false;
        ClearPageSurfaces();
        RebuildTabStrip();
        UpdateControls();
        UpdateStatus();
        UpdateSidebar();
        await RenderCurrentPageAsync(true);
    }

    private void RebuildTabStrip()
    {
        if (TabStrip is null)
        {
            return;
        }
        TabStrip.Children.Clear();
        foreach (PdfSession session in _sessions)
        {
            bool active = ReferenceEquals(session, _activeSession);
            var closeButton = new Button
            {
                Padding = new Thickness(0),
                Width = 20,
                Height = 20,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Content = new SymbolIcon(Symbol.Cancel),
                Tag = session
            };
            closeButton.Click += CloseTab_Click;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            header.Children.Add(new TextBlock
            {
                Text = session.Header,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
            });
            header.Children.Add(closeButton);

            var tab = new Button
            {
                Content = header,
                Tag = session,
                MinWidth = 120,
                MaxWidth = 260,
                MinHeight = 30,
                Padding = new Thickness(10, 5, 6, 5),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = active
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
            };
            tab.Click += Tab_Click;
            TabStrip.Children.Add(tab);
        }
    }

    private async void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is PdfSession session)
        {
            await ActivateSessionAsync(session);
        }
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is PdfSession session)
        {
            await CloseSessionAsync(session);
        }
    }

    private void AddTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _activeSession is { IsEmpty: true })
        {
            UpdateControls();
            return;
        }

        SaveActiveSession();
        CancelPendingWork();

        var session = new PdfSession
        {
            ZoomMode = _zoomMode,
            DisplayMode = _displayMode
        };
        _sessions.Add(session);
        _activeSession = session;
        ApplySession(session);
        ClearPageSurfaces();
        RebuildTabStrip();
        UpdateControls();
        UpdateStatus();
        UpdateSidebar();
    }

    private async Task PickDocumentAsync()
    {
        if (_pickerOpen || _closed)
        {
            return;
        }

        _pickerOpen = true;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".pdf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null && !_closed)
            {
                await OpenDocumentAsync(file.Path);
            }
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Unable to open the file picker", exception);
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    private async Task OpenDocumentAsync(string path)
    {
        if (_closed)
        {
            return;
        }

        int version = ++_openVersion;
        CancelPendingWork();
        _opening = true;
        UpdateControls();
        UpdateStatus();
        PdfDocument? candidate = null;
        PdfSession? session = null;
        string? password = null;
        bool committed = false;
        try
        {
            while (version == _openVersion && !_closed)
            {
                try
                {
                    await _documentGate.WaitAsync();
                    try
                    {
                        candidate = await Task.Run(() => PdfDocument.Open(path, password));
                    }
                    finally
                    {
                        _documentGate.Release();
                    }
                    break;
                }
                catch (PdfException exception) when (exception.IsPasswordRequired)
                {
                    if (version != _openVersion || _closed)
                    {
                        return;
                    }
                    password = await RequestPasswordAsync(Path.GetFileName(path), password is not null);
                    if (password is null)
                    {
                        return;
                    }
                }
            }

            if (candidate is null || version != _openVersion || _closed)
            {
                return;
            }

            await _documentGate.WaitAsync();
            try
            {
                if (version != _openVersion || _closed)
                {
                    return;
                }
                // Keep the previously visible document as a background tab.
                SaveActiveSession();

                // A new tab starts empty so its Open button can load the PDF
                // into that tab instead of leaving a stray "Empty" tab behind.
                PdfSession target = _activeSession is { IsEmpty: true } active
                    ? active
                    : new PdfSession();
                session = target;
                if (!_sessions.Contains(target))
                {
                    _sessions.Add(target);
                }

                target.Document = candidate;
                target.Path = path;
                target.Name = Path.GetFileName(path);
                target.PageCount = candidate.PageCount;
                target.Page = 1;
                target.Rotation = 0;
                target.ZoomMode = _zoomMode;
                target.DisplayMode = _displayMode;
                target.PageSizes = null;
                target.PageLayouts = null;
                target.PageSizesRotation = -1;
                target.SizesComplete = false;
                target.PageText = string.Empty;
                target.LastQuery = string.Empty;
                target.LastMatchPage = 0;
                target.LastMatchOffset = -1;
                target.Outline = null;

                _document = candidate;
                candidate = null;
                _documentName = target.Name;
                _pageCount = target.PageCount;
                _page = 1;
                _rotation = 0;
                _lastQuery = string.Empty;
                _lastMatchPage = 0;
                _lastMatchOffset = -1;
                _searchMessage = string.Empty;
                _pageSizes = null;
                _pageLayouts = null;
                _pageSizesRotation = -1;
                _sizesComplete = false;
                ClearPageSurfaces();
                PageTextBox.Text = string.Empty;
                committed = true;

                _activeSession = target;
                RebuildTabStrip();
                UpdateControls();
            }
            finally
            {
                _documentGate.Release();
            }

            if (version != _openVersion || _closed)
            {
                return;
            }

            _opening = false;
            UpdateControls();
            RootGrid.UpdateLayout();
            await RenderCurrentPageAsync();
            if (version == _openVersion && !_closed && session is not null && session.Outline is null)
            {
                await LoadOutlineAsync(session);
            }
        }
        catch (Exception exception)
        {
            if (version == _openVersion && !_closed)
            {
                await ShowErrorAsync("Unable to open PDF", exception);
            }
        }
        finally
        {
            if (candidate is not null)
            {
                await _documentGate.WaitAsync();
                try
                {
                    await Task.Run(candidate.Dispose);
                }
                finally
                {
                    _documentGate.Release();
                }
            }
            if (version == _openVersion)
            {
                _opening = false;
                UpdateControls();
                UpdateStatus();
                if (!committed && _document is not null && !_closed)
                {
                    await RenderCurrentPageAsync();
                }
            }
        }
    }

    private async Task<T> UseDocumentAsync<T>(PdfDocument document, Func<PdfDocument, T> action, CancellationToken token)
    {
        await _documentGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_closed || !ReferenceEquals(document, _document))
            {
                throw new OperationCanceledException();
            }
            return await Task.Run(() => action(document), token);
        }
        finally
        {
            _documentGate.Release();
        }
    }

    private async Task RenderCurrentPageAsync(bool resetScroll = true) => await RefreshLayoutAsync(resetScroll);

    private async Task RefreshLayoutAsync(bool scrollToCurrent)
    {
        PdfDocument? document = _document;
        if (document is null || _closed || _opening)
        {
            return;
        }

        _layoutCancellation.Cancel();
        _layoutCancellation.Dispose();
        _layoutCancellation = new CancellationTokenSource();
        CancellationToken token = _layoutCancellation.Token;
        int version = ++_layoutVersion;
        _layingOut = true;
        _renderCancellation.Cancel();
        ++_renderVersion;
        UpdateStatus();
        try
        {
            PageSize[] sizes = await GetPageSizesAsync(document, token);
            if (token.IsCancellationRequested || version != _layoutVersion || !ReferenceEquals(document, _document))
            {
                return;
            }

            _activeZoom = CalculateZoom(sizes);
            _pageLayouts = BuildPageLayouts(sizes, _activeZoom, out double canvasWidth, out double canvasHeight);
            PageCanvas.Width = canvasWidth;
            PageCanvas.Height = canvasHeight;
            RepositionAllSurfaces(_pageLayouts);
            ZoomText.Text = _activeZoom.ToString("P0", CultureInfo.CurrentCulture);
            PageScrollViewer.UpdateLayout();
            if (scrollToCurrent)
            {
                ScrollToPage(_page);
            }
            await RenderVisiblePagesAsync();
            if (TextPaneToggle.IsChecked == true)
            {
                await LoadPageTextAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _layoutVersion && !_closed)
            {
                await ShowErrorAsync("Unable to arrange document pages", exception);
            }
        }
        finally
        {
            if (version == _layoutVersion)
            {
                _layingOut = false;
                UpdateStatus();
            }
        }
    }

    private async Task<PageSize[]> GetPageSizesAsync(PdfDocument document, CancellationToken token)
    {
        int rotation = _rotation;
        if (IsContinuousMode())
        {
            if (_sizesComplete && _pageSizes is { Length: > 0 } cached &&
                _pageSizesRotation == rotation && cached.Length == _pageCount)
            {
                return cached;
            }
            PageSize[] sizes = await UseDocumentAsync(document, pdf => pdf.GetAllPageSizes(rotation), token);
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(document, _document) && rotation == _rotation)
            {
                _pageSizes = sizes;
                _pageSizesRotation = rotation;
                _sizesComplete = true;
            }
            return sizes;
        }

        // Single-page / side-by-side-single: only the current page (and its
        // partner) needs measuring, so opening a huge document does not
        // enumerate every page just to show a single page.
        var singleSizes = _pageSizes is { Length: > 0 } && _pageSizes.Length == _pageCount
            ? (PageSize[])_pageSizes.Clone()
            : new PageSize[_pageCount];
        foreach (int page in GetPairPages(_page).Distinct())
        {
            if (page < 1 || page > _pageCount)
            {
                continue;
            }
            token.ThrowIfCancellationRequested();
            singleSizes[page - 1] = await UseDocumentAsync(document, pdf => pdf.GetPageSize(page, rotation), token);
        }
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(document, _document) && rotation == _rotation)
        {
            _pageSizes = singleSizes;
            _pageSizesRotation = rotation;
            _sizesComplete = false;
        }
        return singleSizes;
    }

    private double CalculateZoom(PageSize[] sizes)
    {
        PageSize current = sizes[Math.Clamp(_page - 1, 0, sizes.Length - 1)];
        (double width, double height) = GetDisplaySize(current, 1);
        if (IsSideBySideMode())
        {
            int first = GetPairStart(_page);
            (double leftWidth, double leftHeight) = GetDisplaySize(sizes[first - 1], 1);
            (double rightWidth, double rightHeight) = first < sizes.Length
                ? GetDisplaySize(sizes[first], 1)
                : (0, 0);
            width = leftWidth + (rightWidth > 0 ? PageGap + rightWidth : 0);
            height = Math.Max(leftHeight, rightHeight);
        }

        double availableWidth = Math.Max(64, GetViewportWidth() - PageMargin * 2);
        double availableHeight = Math.Max(64, GetViewportHeight() - PageMargin * 2);
        double zoom = _zoomMode switch
        {
            0 => Math.Min(availableWidth / width, availableHeight / height),
            1 => availableWidth / width,
            2 => 0.25,
            3 => 0.50,
            4 => 0.75,
            5 => 1.00,
            6 => 1.25,
            7 => 1.50,
            8 => 2.00,
            9 => 3.00,
            _ => 4.00
        };
        return Math.Clamp(zoom, 0.05, 4.0);
    }

    private PageLayout[] BuildPageLayouts(PageSize[] sizes, double zoom, out double canvasWidth, out double canvasHeight)
    {
        var layouts = new PageLayout[sizes.Length];
        var dimensions = sizes.Select(size => GetDisplaySize(size, zoom)).ToArray();
        double viewportWidth = GetViewportWidth();
        double viewportHeight = GetViewportHeight();

        if (_displayMode == ReaderDisplayMode.VerticalContinuous)
        {
            double widest = dimensions.Max(size => size.width);
            canvasWidth = Math.Max(viewportWidth, widest + PageMargin * 2);
            double y = PageMargin;
            for (int index = 0; index < dimensions.Length; index++)
            {
                var size = dimensions[index];
                layouts[index] = new PageLayout((canvasWidth - size.width) / 2, y, size.width, size.height);
                y += size.height + PageGap;
            }
            canvasHeight = Math.Max(viewportHeight, y - PageGap + PageMargin);
            return layouts;
        }

        if (_displayMode == ReaderDisplayMode.HorizontalContinuous)
        {
            double tallest = dimensions.Max(size => size.height);
            canvasHeight = Math.Max(viewportHeight, tallest + PageMargin * 2);
            double x = PageMargin;
            for (int index = 0; index < dimensions.Length; index++)
            {
                var size = dimensions[index];
                layouts[index] = new PageLayout(x, (canvasHeight - size.height) / 2, size.width, size.height);
                x += size.width + PageGap;
            }
            canvasWidth = Math.Max(viewportWidth, x - PageGap + PageMargin);
            return layouts;
        }

        if (_displayMode == ReaderDisplayMode.SideBySideContinuous)
        {
            double leftColumn = dimensions.Where((_, index) => index % 2 == 0).Max(size => size.width);
            double rightColumn = dimensions.Where((_, index) => index % 2 == 1).DefaultIfEmpty().Max(size => size.width);
            double contentWidth = leftColumn + (rightColumn > 0 ? PageGap + rightColumn : 0);
            canvasWidth = Math.Max(viewportWidth, contentWidth + PageMargin * 2);
            double y = PageMargin;
            for (int first = 0; first < dimensions.Length; first += 2)
            {
                var left = dimensions[first];
                var right = first + 1 < dimensions.Length ? dimensions[first + 1] : (0d, 0d);
                double rowHeight = Math.Max(left.height, right.Item2);
                double startX = (canvasWidth - contentWidth) / 2;
                layouts[first] = new PageLayout(startX + leftColumn - left.width, y, left.width, left.height);
                if (first + 1 < dimensions.Length)
                {
                    layouts[first + 1] = new PageLayout(startX + leftColumn + PageGap, y, right.Item1, right.Item2);
                }
                y += rowHeight + PageGap;
            }
            canvasHeight = Math.Max(viewportHeight, y - PageGap + PageMargin);
            return layouts;
        }

        int pairStart = _displayMode == ReaderDisplayMode.SideBySideSingle ? GetPairStart(_page) : _page;
        var firstSize = dimensions[pairStart - 1];
        var secondSize = _displayMode == ReaderDisplayMode.SideBySideSingle && pairStart < dimensions.Length
            ? dimensions[pairStart]
            : (0d, 0d);
        double contentWidthSingle = firstSize.width + (secondSize.Item1 > 0 ? PageGap + secondSize.Item1 : 0);
        double contentHeightSingle = Math.Max(firstSize.height, secondSize.Item2);
        canvasWidth = Math.Max(viewportWidth, contentWidthSingle + PageMargin * 2);
        canvasHeight = Math.Max(viewportHeight, contentHeightSingle + PageMargin * 2);
        double start = (canvasWidth - contentWidthSingle) / 2;
        layouts[pairStart - 1] = new PageLayout(start, (canvasHeight - firstSize.height) / 2, firstSize.width, firstSize.height);
        if (secondSize.Item1 > 0)
        {
            layouts[pairStart] = new PageLayout(start + firstSize.width + PageGap,
                (canvasHeight - secondSize.Item2) / 2, secondSize.Item1, secondSize.Item2);
        }
        return layouts;
    }

    private async Task RenderVisiblePagesAsync()
    {
        PdfDocument? document = _document;
        if (document is null || _pageLayouts is null || _pageSizes is null || _closed)
        {
            return;
        }

        List<int> strict = GetStrictVisiblePageNumbers();
        List<int> window = GetVisiblePageNumbers();
        if (window.Count == 0)
        {
            return;
        }
        List<int> prefetch = GetPrefetchPages(window);
        List<int> target = window.Union(prefetch).ToList();

        _renderCancellation.Cancel();
        _renderCancellation.Dispose();
        _renderCancellation = new CancellationTokenSource();
        CancellationToken token = _renderCancellation.Token;
        int version = ++_renderVersion;
        int layoutVersion = _layoutVersion;
        int rotation = _rotation;
        double zoom = _activeZoom;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        _rendering = true;
        UpdateStatus();

        HashSet<int> strictSet = strict.ToHashSet();

        try
        {
            RemoveSurfacesExcept(target);
            // Foreground: the page(s) actually under the viewport, focused page
            // first, so what you are reading appears as quickly as possible.
            foreach (int page in target.OrderBy(PageCenterDistance).Where(strictSet.Contains))
            {
                await RenderOnePageAsync(document, page, zoom, scale, rotation, token, version, layoutVersion);
            }
            if (version == _renderVersion)
            {
                _rendering = false;
                UpdateStatus();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _renderVersion && !_closed)
            {
                await ShowErrorAsync("Unable to render page", exception);
            }
        }
        finally
        {
            if (version == _renderVersion)
            {
                _rendering = false;
                UpdateStatus();
            }
        }

        // Quietly pre-render the resident window in the background so scrolling
        // reaches already-rasterised pages. Detached so it never blocks open.
        _ = PreRenderWindowAsync(document, target, strictSet, zoom, scale, rotation,
            token, version, layoutVersion);
    }

    private async Task RenderOnePageAsync(PdfDocument document, int page, double zoom, double scale,
        int rotation, CancellationToken token, int version, int layoutVersion)
    {
        token.ThrowIfCancellationRequested();
        PageSize size = _pageSizes![page - 1];
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        PageLayout layout = _pageLayouts![page - 1];
        if (layout.Width <= 0 || layout.Height <= 0)
        {
            return;
        }

        double renderDpi = ComputeRenderDpi(size, zoom, scale, out _, out _);
        int onScreenWidth = Math.Max(1, (int)Math.Round(layout.Width * scale));
        int onScreenHeight = Math.Max(1, (int)Math.Round(layout.Height * scale));

        // Reuse (and scale) an existing bitmap whenever it is large enough for
        // the on-screen size, so zoom/resize/sidebar toggles never re-rasterize
        // an already-rendered page (critical for decoder-bound scanned pages).
        if (_pageSurfaces.TryGetValue(page, out PageSurface? existing) && existing.Bitmap is not null &&
            existing.PixelWidth >= onScreenWidth * ReuseResolutionTolerance &&
            existing.PixelHeight >= onScreenHeight * ReuseResolutionTolerance)
        {
            PositionSurface(existing, layout);
            return;
        }

        RenderedPage bitmap = await UseDocumentAsync(document, pdf =>
            pdf.RenderPage(page, renderDpi, rotation), token);
        if (token.IsCancellationRequested || version != _renderVersion || layoutVersion != _layoutVersion ||
            !ReferenceEquals(document, _document))
        {
            return;
        }
        PageSurface surface = EnsurePageSurface(page, layout, CreateBitmap(bitmap), bitmap.Width, bitmap.Height);
        AutomationProperties.SetName(surface.Image, $"PDF page {page} of {_pageCount}");
    }

    private async Task PreRenderWindowAsync(PdfDocument document, List<int> target, HashSet<int> strictSet,
        double zoom, double scale, int rotation, CancellationToken token, int version, int layoutVersion)
    {
        try
        {
            IEnumerable<int> pending = target
                .OrderBy(PageCenterDistance)
                .Where(page => !strictSet.Contains(page));
            foreach (int page in pending)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                await RenderOnePageAsync(document, page, zoom, scale, rotation, token, version, layoutVersion);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private WriteableBitmap CreateBitmap(RenderedPage bitmap)
    {
        var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using Stream stream = image.PixelBuffer.AsStream();
        int rowBytes = checked(bitmap.Width * 4);
        for (int row = 0; row < bitmap.Height; row++)
        {
            stream.Write(bitmap.Pixels, checked(row * bitmap.Stride), rowBytes);
        }
        image.Invalidate();
        return image;
    }

    private static WriteableBitmap CreateThumbnail(RenderedPage bitmap)
    {
        var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using Stream stream = image.PixelBuffer.AsStream();
        int rowBytes = checked(bitmap.Width * 4);
        for (int row = 0; row < bitmap.Height; row++)
        {
            stream.Write(bitmap.Pixels, checked(row * bitmap.Stride), rowBytes);
        }
        image.Invalidate();
        return image;
    }

    private PageSurface EnsurePageSurface(int page, PageLayout layout, WriteableBitmap bitmap,
        int pixelWidth, int pixelHeight)
    {
        if (!_pageSurfaces.TryGetValue(page, out PageSurface? surface))
        {
            var image = new Image { Stretch = Stretch.Fill };
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0)),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                Child = image
            };
            PageCanvas.Children.Add(border);
            surface = new PageSurface(border, image);
            _pageSurfaces[page] = surface;
        }
        surface.Bitmap = bitmap;
        surface.PixelWidth = pixelWidth;
        surface.PixelHeight = pixelHeight;
        surface.Image.Source = bitmap;
        PositionSurface(surface, layout);
        return surface;
    }

    private static void PositionSurface(PageSurface surface, PageLayout layout)
    {
        surface.Container.Width = layout.Width;
        surface.Container.Height = layout.Height;
        surface.Image.Width = layout.Width;
        surface.Image.Height = layout.Height;
        Canvas.SetLeft(surface.Container, layout.X);
        Canvas.SetTop(surface.Container, layout.Y);
    }

    private void RepositionAllSurfaces(PageLayout[] layouts)
    {
        var remove = new List<int>();
        foreach ((int page, PageSurface surface) in _pageSurfaces)
        {
            if (page < 1 || page > layouts.Length)
            {
                remove.Add(page);
                continue;
            }
            PageLayout layout = layouts[page - 1];
            if (layout.Width <= 0 || layout.Height <= 0)
            {
                remove.Add(page);
                continue;
            }
            PositionSurface(surface, layout);
        }
        foreach (int page in remove)
        {
            if (_pageSurfaces.Remove(page, out PageSurface? surface))
            {
                PageCanvas.Children.Remove(surface.Container);
            }
        }
    }

    private static double ComputeRenderDpi(PageSize size, double zoom, double scale,
        out int bitmapWidth, out int bitmapHeight)
    {
        // Render once at a base quality that leaves room for moderate zoom; the
        // compositor scales the cached bitmap for zoom/resize. Decoder-bound
        // (scanned) pages cost the same at any DPI, so a higher base is free.
        double baseDpi = Math.Max(150, 96 * scale);
        double dpi = Math.Max(baseDpi, 96 * zoom * scale);
        double width = size.Width * dpi / 72;
        double height = size.Height * dpi / 72;
        double reduction = Math.Min(1, Math.Min(4500 / Math.Max(width, height),
            Math.Sqrt(8_000_000 / (width * height))));
        double renderDpi = dpi * reduction;
        bitmapWidth = Math.Max(1, (int)Math.Round(size.Width * renderDpi / 72));
        bitmapHeight = Math.Max(1, (int)Math.Round(size.Height * renderDpi / 72));
        return renderDpi;
    }

    private void RemoveSurfacesExcept(IReadOnlyCollection<int> pages)
    {
        foreach (int page in _pageSurfaces.Keys.Where(page => !pages.Contains(page)).ToArray())
        {
            PageCanvas.Children.Remove(_pageSurfaces[page].Container);
            _pageSurfaces.Remove(page);
        }
    }

    private void ClearPageSurfaces()
    {
        _pageSurfaces.Clear();
        PageCanvas.Children.Clear();
    }

    private List<int> GetVisiblePageNumbers()
    {
        if (_pageLayouts is null)
        {
            return [];
        }
        if (!IsContinuousMode())
        {
            return GetPairPages(_page);
        }

        double viewportWidth = GetViewportWidth();
        double viewportHeight = GetViewportHeight();
        double visibleX = PageScrollViewer.HorizontalOffset;
        double visibleY = PageScrollViewer.VerticalOffset;
        double x = visibleX - viewportWidth;
        double y = visibleY - viewportHeight;
        double width = viewportWidth * 3;
        double height = viewportHeight * 3;
        double centerX = visibleX + viewportWidth / 2;
        double centerY = visibleY + viewportHeight / 2;
        var pages = new HashSet<int>();
        var nearbyPages = new List<(int Page, double Distance)>();

        void AddPageAndPartner(int page)
        {
            pages.Add(page);
            if (IsSideBySideMode())
            {
                int partner = page % 2 == 0 ? page - 1 : page + 1;
                if (partner <= _pageCount)
                {
                    pages.Add(partner);
                }
            }
        }

        for (int index = 0; index < _pageLayouts.Length; index++)
        {
            PageLayout layout = _pageLayouts[index];
            if (layout.Width > 0 && Intersects(layout, x, y, width, height))
            {
                int page = index + 1;
                double dx = centerX < layout.X ? layout.X - centerX :
                    centerX > layout.X + layout.Width ? centerX - (layout.X + layout.Width) : 0;
                double dy = centerY < layout.Y ? layout.Y - centerY :
                    centerY > layout.Y + layout.Height ? centerY - (layout.Y + layout.Height) : 0;
                nearbyPages.Add((page, dx * dx + dy * dy));
                if (Intersects(layout, visibleX, visibleY, viewportWidth, viewportHeight))
                {
                    AddPageAndPartner(page);
                }
            }
        }

        if (pages.Count == 0)
        {
            AddPageAndPartner(_page);
        }

        int residentBudget = Math.Max(MaxResidentPages, pages.Count);
        foreach (var candidate in nearbyPages.OrderBy(candidate => candidate.Distance))
        {
            if (pages.Count >= residentBudget)
            {
                break;
            }
            AddPageAndPartner(candidate.Page);
        }
        return pages.OrderBy(page => page).ToList();
    }

    private List<int> GetStrictVisiblePageNumbers()
    {
        if (_pageLayouts is null)
        {
            return [];
        }
        if (!IsContinuousMode())
        {
            return GetPairPages(_page);
        }

        double visibleX = PageScrollViewer.HorizontalOffset;
        double visibleY = PageScrollViewer.VerticalOffset;
        double viewportWidth = GetViewportWidth();
        double viewportHeight = GetViewportHeight();
        var pages = new HashSet<int>();

        void AddPageAndPartner(int page)
        {
            pages.Add(page);
            if (IsSideBySideMode())
            {
                int partner = page % 2 == 0 ? page - 1 : page + 1;
                if (partner <= _pageCount)
                {
                    pages.Add(partner);
                }
            }
        }

        for (int index = 0; index < _pageLayouts.Length; index++)
        {
            PageLayout layout = _pageLayouts[index];
            if (layout.Width > 0 && Intersects(layout, visibleX, visibleY, viewportWidth, viewportHeight))
            {
                AddPageAndPartner(index + 1);
            }
        }
        if (pages.Count == 0)
        {
            AddPageAndPartner(_page);
        }
        return pages.OrderBy(page => page).ToList();
    }

    private List<int> GetPrefetchPages(IReadOnlyList<int> visible)
    {
        if (!IsContinuousMode() || visible.Count == 0 || _pageLayouts is null)
        {
            return [];
        }
        int minPage = visible.Min();
        int maxPage = visible.Max();
        var prefetch = new List<int>();
        for (int page = maxPage + 1; page <= Math.Min(_pageCount, maxPage + PrefetchPages); page++)
        {
            PageLayout layout = _pageLayouts[page - 1];
            if (layout.Width > 0)
            {
                prefetch.Add(page);
            }
        }
        for (int page = minPage - 1; page >= Math.Max(1, minPage - PrefetchPages); page--)
        {
            PageLayout layout = _pageLayouts[page - 1];
            if (layout.Width > 0)
            {
                prefetch.Add(page);
            }
        }
        return prefetch;
    }

    private double PageCenterDistance(int page)
    {
        if (_pageLayouts is null || page < 1 || page > _pageLayouts.Length)
        {
            return double.MaxValue;
        }
        PageLayout layout = _pageLayouts[page - 1];
        if (layout.Width <= 0 || layout.Height <= 0)
        {
            return double.MaxValue;
        }
        double centerX = PageScrollViewer.HorizontalOffset + GetViewportWidth() / 2;
        double centerY = PageScrollViewer.VerticalOffset + GetViewportHeight() / 2;
        double dx = centerX < layout.X ? layout.X - centerX :
            centerX > layout.X + layout.Width ? centerX - (layout.X + layout.Width) : 0;
        double dy = centerY < layout.Y ? layout.Y - centerY :
            centerY > layout.Y + layout.Height ? centerY - (layout.Y + layout.Height) : 0;
        return dx * dx + dy * dy;
    }

    private async Task NavigateToPageAsync(int page, bool cancelSearch = true)
    {
        if (_document is null || _opening || page < 1 || page > _pageCount)
        {
            UpdateControls();
            return;
        }
        if (cancelSearch)
        {
            _searchCancellation.Cancel();
            ++_searchVersion;
            _searching = false;
        }
        if (_displayMode == ReaderDisplayMode.SideBySideSingle)
        {
            page = GetPairStart(page);
        }
        _page = page;
        _searchMessage = string.Empty;
        PageTextBox.Text = string.Empty;
        UpdateControls();
        UpdateStatus();
        if (_pageLayouts is null || !IsContinuousMode())
        {
            await RefreshLayoutAsync(true);
        }
        else
        {
            ScrollToPage(page);
            await RenderVisiblePagesAsync();
            if (TextPaneToggle.IsChecked == true)
            {
                await LoadPageTextAsync();
            }
        }
    }

    private async Task UpdateViewportAsync()
    {
        if (_closed || _document is null || _pageLayouts is null || !IsContinuousMode())
        {
            return;
        }

        int page = GetViewportCenterPage();
        if (page != _page)
        {
            _page = page;
            _searchMessage = string.Empty;
            PageTextBox.Text = string.Empty;
            UpdateControls();
            UpdateStatus();
            if (TextPaneToggle.IsChecked == true)
            {
                await LoadPageTextAsync();
            }
        }
        await RenderVisiblePagesAsync();
    }

    private int GetViewportCenterPage()
    {
        if (_pageLayouts is null)
        {
            return _page;
        }

        double centerX = PageScrollViewer.HorizontalOffset + GetViewportWidth() / 2;
        double centerY = PageScrollViewer.VerticalOffset + GetViewportHeight() / 2;
        int closestPage = _page;
        double closestDistance = double.MaxValue;
        for (int index = 0; index < _pageLayouts.Length; index++)
        {
            PageLayout layout = _pageLayouts[index];
            if (layout.Width <= 0 || layout.Height <= 0)
            {
                continue;
            }
            double dx = centerX < layout.X ? layout.X - centerX :
                centerX > layout.X + layout.Width ? centerX - (layout.X + layout.Width) : 0;
            double dy = centerY < layout.Y ? layout.Y - centerY :
                centerY > layout.Y + layout.Height ? centerY - (layout.Y + layout.Height) : 0;
            double distance = dx * dx + dy * dy;
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestPage = index + 1;
            }
        }
        return closestPage;
    }

    private void ScrollToPage(int page)
    {
        if (_pageLayouts is null || page < 1 || page > _pageLayouts.Length)
        {
            return;
        }
        PageLayout layout = _pageLayouts[page - 1];
        if (layout.Width <= 0)
        {
            return;
        }
        double? x = _displayMode == ReaderDisplayMode.HorizontalContinuous ? Math.Max(0, layout.X - PageMargin) : 0;
        double? y = _displayMode is ReaderDisplayMode.VerticalContinuous or ReaderDisplayMode.SideBySideContinuous
            ? Math.Max(0, layout.Y - PageMargin)
            : 0;
        PageScrollViewer.ChangeView(x, y, null, true);
    }

    private List<int> GetPairPages(int page)
    {
        if (_displayMode != ReaderDisplayMode.SideBySideSingle)
        {
            return [page];
        }
        int first = GetPairStart(page);
        return first < _pageCount ? [first, first + 1] : [first];
    }

    private int GetPairStart(int page) => page % 2 == 0 ? page - 1 : page;

    private int GetPreviousPage() => IsSideBySideMode() ? GetPairStart(_page) - 2 : _page - 1;

    private int GetNextPage() => IsSideBySideMode() ? GetPairStart(_page) + 2 : _page + 1;

    private bool IsContinuousMode() => _displayMode is ReaderDisplayMode.VerticalContinuous
        or ReaderDisplayMode.SideBySideContinuous or ReaderDisplayMode.HorizontalContinuous;

    private bool IsSideBySideMode() => _displayMode is ReaderDisplayMode.SideBySideSingle
        or ReaderDisplayMode.SideBySideContinuous;

    private static (double width, double height) GetDisplaySize(PageSize size, double zoom) =>
        (size.Width * 96 / 72 * zoom, size.Height * 96 / 72 * zoom);

    private double GetViewportWidth() => Math.Max(64, PageScrollViewer.ViewportWidth > 0
        ? PageScrollViewer.ViewportWidth : PageScrollViewer.ActualWidth);

    private double GetViewportHeight() => Math.Max(64, PageScrollViewer.ViewportHeight > 0
        ? PageScrollViewer.ViewportHeight : PageScrollViewer.ActualHeight);

    private static bool Intersects(PageLayout layout, double x, double y, double width, double height) =>
        layout.X < x + width && layout.X + layout.Width > x && layout.Y < y + height && layout.Y + layout.Height > y;

    private async Task LoadPageTextAsync()
    {
        PdfDocument? document = _document;
        if (document is null || _closed)
        {
            return;
        }
        _textCancellation.Cancel();
        _textCancellation.Dispose();
        _textCancellation = new CancellationTokenSource();
        CancellationToken token = _textCancellation.Token;
        int version = ++_textVersion;
        int page = _page;
        try
        {
            string text = await UseDocumentAsync(document, pdf => pdf.GetPageText(page), token);
            if (version == _textVersion && page == _page && !_closed)
            {
                if (PageTextBox.Text != text)
                {
                    PageTextBox.Text = text;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _textVersion && !_closed)
            {
                await ShowErrorAsync("Unable to read page text", exception);
            }
        }
    }

    private async Task FindNextAsync()
    {
        PdfDocument? document = _document;
        string query = SearchBox.Text;
        if (document is null || _opening || string.IsNullOrWhiteSpace(query) || _closed)
        {
            return;
        }

        _searchCancellation.Cancel();
        _searchCancellation.Dispose();
        _searchCancellation = new CancellationTokenSource();
        CancellationToken token = _searchCancellation.Token;
        int version = ++_searchVersion;
        int startPage = _page;
        int pageCount = _pageCount;
        int startOffset = query == _lastQuery && _lastMatchPage == startPage ? _lastMatchOffset + query.Length : 0;
        _searching = true;
        _searchMessage = string.Empty;
        UpdateStatus();
        try
        {
            for (int index = 0; index < pageCount + (startOffset > 0 ? 1 : 0); index++)
            {
                token.ThrowIfCancellationRequested();
                int page = (startPage - 1 + index) % pageCount + 1;
                string text = await UseDocumentAsync(document, pdf => pdf.GetPageText(page), token);
                token.ThrowIfCancellationRequested();
                int offset = text.IndexOf(query, index == 0 ? Math.Min(startOffset, text.Length) : 0,
                    StringComparison.CurrentCultureIgnoreCase);
                if (offset < 0 || (index == pageCount && offset >= startOffset))
                {
                    continue;
                }

                _lastQuery = query;
                _lastMatchPage = page;
                _lastMatchOffset = offset;
                await NavigateToPageAsync(page, false);
                token.ThrowIfCancellationRequested();
                _updatingUi = true;
                TextPaneToggle.IsChecked = true;
                _updatingUi = false;
                UpdateTextPaneLayout();
                PageTextBox.Text = text;
                PageTextBox.Select(offset, query.Length);
                _searchMessage = $"Found on page {page}";
                return;
            }
            _searchMessage = "No matches found";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _searchVersion && !_closed)
            {
                await ShowErrorAsync("Unable to search document", exception);
            }
        }
        finally
        {
            if (version == _searchVersion)
            {
                _searching = false;
                UpdateStatus();
            }
        }
    }

    private void UpdateControls()
    {
        if (_closed)
        {
            return;
        }
        bool hasDocument = _document is not null;
        bool canRead = hasDocument && !_opening;
        CloseButton.IsEnabled = hasDocument;
        PreviousButton.IsEnabled = canRead && GetPreviousPage() >= 1;
        NextButton.IsEnabled = canRead && GetNextPage() <= _pageCount;
        PageNumberBox.IsEnabled = canRead;
        PageNumberBox.Text = hasDocument ? _page.ToString(CultureInfo.InvariantCulture) : "0";
        PageCountText.Text = $"/ {_pageCount}";
        ZoomSelector.IsEnabled = canRead;
        DisplayModeSelector.IsEnabled = canRead;
        RotateButton.IsEnabled = canRead;
        FindToggle.IsEnabled = canRead;
        TextPaneToggle.IsEnabled = canRead;
        CopyTextButton.IsEnabled = canRead;
        SearchBox.IsEnabled = canRead;
        FindNextButton.IsEnabled = canRead && !string.IsNullOrWhiteSpace(SearchBox.Text);
        EmptyState.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        PageScrollViewer.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitleBar();
        UpdateTextPaneLayout();
    }

    private void UpdateTitleBar()
    {
        Title = _document is not null && !string.IsNullOrEmpty(_documentName)
            ? $"{_documentName} - Xpdf"
            : "Xpdf";
    }

    private void UpdateTitleBarInsets()
    {
        if (CaptionButtonsSpacer is null)
        {
            return;
        }
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            CaptionButtonsSpacer.Width = new GridLength(0);
            return;
        }
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionButtonsSpacer.Width = new GridLength(
            Math.Max(0, AppWindow.TitleBar.RightInset / Math.Max(1.0, scale)));
    }

    private void QueueTitleBarRegionsUpdate()
    {
        if (_closed || _titleBarRegionsPending)
        {
            return;
        }
        _titleBarRegionsPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _titleBarRegionsPending = false;
                UpdateTitleBarRegions();
            }))
        {
            _titleBarRegionsPending = false;
        }
    }

    private void UpdateTitleBarRegions()
    {
        InputNonClientPointerSource? source = _titleBarInputSource;
        if (_closed || source is null || RootGrid.XamlRoot is null)
        {
            return;
        }

        double scale = Math.Max(1.0, RootGrid.XamlRoot.RasterizationScale);
        var regions = new List<Windows.Graphics.RectInt32>();
        foreach (UIElement child in TabStrip.Children)
        {
            if (child is FrameworkElement element)
            {
                AddTitleBarPassthroughRegion(element, regions, scale);
            }
        }
        AddTitleBarPassthroughRegion(AddTabButton, regions, scale);

        try
        {
            source.SetRegionRects(NonClientRegionKind.Passthrough, regions.ToArray());
        }
        catch (Exception)
        {
            // Older Windows builds can ignore custom caption hit testing.
        }
    }

    private static void AddTitleBarPassthroughRegion(
        FrameworkElement element,
        ICollection<Windows.Graphics.RectInt32> regions,
        double scale)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            Windows.Foundation.Rect bounds = element.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            int left = (int)Math.Floor(bounds.X * scale);
            int top = (int)Math.Floor(bounds.Y * scale);
            int right = (int)Math.Ceiling(bounds.Right * scale);
            int bottom = (int)Math.Ceiling(bounds.Bottom * scale);
            if (right > left && bottom > top)
            {
                regions.Add(new Windows.Graphics.RectInt32
                {
                    X = left,
                    Y = top,
                    Width = right - left,
                    Height = bottom - top
                });
            }
        }
        catch (Exception)
        {
            // The element can disappear between a layout pass and this callback.
        }
    }

    private void UpdateTextPaneLayout()
    {
        bool visible = _document is not null && TextPaneToggle.IsChecked == true && DocumentArea.ActualWidth >= 480;
        TextPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TextPaneColumn.Width = new GridLength(visible
            ? Math.Min(360, Math.Max(200, DocumentArea.ActualWidth * 0.42))
            : 0);
    }

    private void UpdateStatus()
    {
        if (_closed)
        {
            return;
        }
        bool busy = _opening || _layingOut || _rendering || _searching || _loadingOutline;
        BusyIndicator.IsActive = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _opening ? "Opening document..." : _loadingOutline ? "Reading outline..." :
            _layingOut ? "Arranging pages..." : _searching ? "Searching..." : _rendering ? "Rendering page..." :
            !string.IsNullOrEmpty(_searchMessage) ? _searchMessage : _document is null ? "No document open" :
            $"{_documentName}  |  Page {_page} of {_pageCount}";
    }

    private void CancelPendingWork()
    {
        _renderCancellation.Cancel();
        _searchCancellation.Cancel();
        _textCancellation.Cancel();
        _copyCancellation.Cancel();
        _layoutCancellation.Cancel();
        ++_renderVersion;
        ++_layoutVersion;
        ++_textVersion;
        ++_searchVersion;
        _rendering = false;
        _searching = false;
        _layingOut = false;
        _loadingOutline = false;
        _resizeTimer.Stop();
        _viewportTimer.Stop();
    }

    private void UpdateSidebar()
    {
        bool visible = _sidebarVisible && _document is not null && _sessions.Count > 0;
        SidebarColumn.Width = new GridLength(visible ? 256 : 0);
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible && _activeSession is not null)
        {
            PopulateSidebar(_activeSession);
        }
        SetSidebarMode(_sidebarOutlineMode);
    }

    private void SetSidebarMode(bool outline)
    {
        _sidebarOutlineMode = outline;
        if (OutlineModeToggle is null || ThumbnailsModeToggle is null)
        {
            return;
        }
        OutlineModeToggle.IsChecked = outline;
        ThumbnailsModeToggle.IsChecked = !outline;
        OutlineTree.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
        ThumbnailsList.Visibility = !outline ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PopulateSidebar(PdfSession session)
    {
        BuildOutlineTree(session);
        BuildThumbnailList(session);
    }

    private void BuildOutlineTree(PdfSession session)
    {
        if (OutlineTree is null)
        {
            return;
        }
        OutlineTree.RootNodes.Clear();
        if (session.Outline is not { Count: > 0 })
        {
            return;
        }
        var stack = new Stack<(int Depth, TreeViewNode Node)>();
        foreach (OutlineEntry entry in session.Outline)
        {
            while (stack.Count > 0 && stack.Peek().Depth >= entry.Depth)
            {
                stack.Pop();
            }
            var node = new TreeViewNode
            {
                Content = new OutlineNodeModel(entry.Title, entry.Page),
                IsExpanded = true
            };
            if (stack.Count == 0)
            {
                OutlineTree.RootNodes.Add(node);
            }
            else
            {
                stack.Peek().Node.Children.Add(node);
            }
            stack.Push((entry.Depth, node));
        }
    }

    private void BuildThumbnailList(PdfSession session)
    {
        _thumbnailItems.Clear();
        _thumbnailCache.TryGetValue(session.Id, out Dictionary<int, ImageSource?>? cache);
        for (int page = 1; page <= session.PageCount; page++)
        {
            ImageSource? image = null;
            if (cache is not null)
            {
                cache.TryGetValue(page, out image);
            }
            _thumbnailItems.Add(new ThumbnailItem(page, image));
        }
        ThumbnailsList.ItemsSource = _thumbnailItems;
    }

    private async void ThumbnailsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item || item.Image is not null)
        {
            return;
        }
        await RenderThumbnailAsync(item);
    }

    private async Task RenderThumbnailAsync(ThumbnailItem item)
    {
        PdfSession? session = _activeSession;
        PdfDocument? doc = session?.Document;
        if (session is null || doc is null || _closed)
        {
            return;
        }
        if (_thumbnailCache.TryGetValue(session.Id, out Dictionary<int, ImageSource?>? cache) &&
            cache.TryGetValue(item.Page, out ImageSource? cached) && cached is not null)
        {
            item.Image = cached;
            return;
        }
        try
        {
            PageSize size = session.PageSizes is { Length: > 0 }
                ? session.PageSizes[item.Page - 1]
                : await UseDocumentAsync(doc, pdf => pdf.GetPageSize(item.Page, session.Rotation), CancellationToken.None);
            double zoom = 110 / Math.Max(1, size.Width * 96 / 72);
            double dpi = 96 * Math.Clamp(zoom, 0.10, 0.60);
            RenderedPage bitmap = await UseDocumentAsync(doc, pdf => pdf.RenderPage(item.Page, dpi, session.Rotation), CancellationToken.None);
            if (ReferenceEquals(session, _activeSession))
            {
                ImageSource image = CreateThumbnail(bitmap);
                if (!_thumbnailCache.TryGetValue(session.Id, out Dictionary<int, ImageSource?>? target))
                {
                    target = new Dictionary<int, ImageSource?>();
                    _thumbnailCache[session.Id] = target;
                }
                target[item.Page] = image;
                item.Image = image;
            }
        }
        catch (Exception)
        {
        }
    }

    private async void ThumbnailsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailsList.SelectedItem is ThumbnailItem item && item.Page >= 1)
        {
            await NavigateToPageAsync(item.Page);
        }
    }

    private void SidebarModeToggle_Click(object sender, RoutedEventArgs e)
    {
        bool outline = ReferenceEquals(sender, OutlineModeToggle);
        SetSidebarMode(outline);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarVisible = !_sidebarVisible;
        UpdateSidebar();
    }

    private async void OutlineTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: OutlineNodeModel model } && model.Page >= 1)
        {
            await NavigateToPageAsync(model.Page);
        }
    }

    private async Task LoadOutlineAsync(PdfSession session)
    {
        PdfDocument? doc = session.Document;
        if (doc is null || _closed || session.Outline is not null)
        {
            return;
        }
        _loadingOutline = true;
        UpdateStatus();
        try
        {
            List<OutlineEntry> outline = await UseDocumentAsync(doc, pdf => pdf.GetOutline(), CancellationToken.None);
            if (ReferenceEquals(session, _activeSession))
            {
                session.Outline = outline;
                BuildOutlineTree(session);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _loadingOutline = false;
            UpdateStatus();
        }
    }

    private async Task CloseDocumentAsync() => await CloseSessionAsync(_activeSession);

    private async Task CloseSessionAsync(PdfSession? session)
    {
        if (session is null || _closed)
        {
            return;
        }
        int version = ++_openVersion;
        CancelPendingWork();
        _opening = false;
        await _documentGate.WaitAsync();
        try
        {
            if (version != _openVersion)
            {
                return;
            }
            int index = _sessions.IndexOf(session);
            if (index < 0)
            {
                return;
            }
            bool wasActive = ReferenceEquals(session, _activeSession);
            _sessions.RemoveAt(index);
            _thumbnailCache.Remove(session.Id);
            session.Dispose();

            if (wasActive)
            {
                _searchCancellation.Cancel();
                _searchMessage = string.Empty;
                FindToggle.IsChecked = false;
                TextPaneToggle.IsChecked = false;
                if (_sessions.Count == 0)
                {
                    _activeSession = null;
                    _document = null;
                    _documentName = string.Empty;
                    _pageCount = 0;
                    _page = 1;
                    _rotation = 0;
                    _zoomMode = 0;
                    _displayMode = ReaderDisplayMode.Single;
                    _pageSizes = null;
                    _pageLayouts = null;
                    _pageSizesRotation = -1;
                    _lastQuery = string.Empty;
                    _lastMatchOffset = -1;
                    ClearPageSurfaces();
                    PageTextBox.Text = string.Empty;
                    ZoomText.Text = string.Empty;
                }
                else
                {
                    int next = index >= _sessions.Count ? _sessions.Count - 1 : index;
                    _activeSession = _sessions[next];
                    ApplySession(_activeSession);
                    ClearPageSurfaces();
                }
                RebuildTabStrip();
                UpdateControls();
                UpdateStatus();
                UpdateSidebar();
                if (_activeSession is not null)
                {
                    await RenderCurrentPageAsync(false);
                }
            }
            else
            {
                RebuildTabStrip();
            }
        }
        finally
        {
            _documentGate.Release();
        }
    }

    private async Task<string?> RequestPasswordAsync(string fileName, bool retry)
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (_closed)
            {
                return null;
            }
            var passwordBox = new PasswordBox { PlaceholderText = "Password", MinWidth = 260 };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = retry ? "The password was not accepted. Try again." : fileName,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(passwordBox);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Password required",
                Content = content,
                PrimaryButtonText = "Open",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.Opened += (_, _) => passwordBox.Focus(FocusState.Programmatic);
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? passwordBox.Password : null;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task ShowErrorAsync(string title, Exception exception)
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (_closed)
            {
                return;
            }
            string message = exception is PdfException { IsPermissionDenied: true }
                ? "This document does not allow text extraction."
                : exception.Message;
            await new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close"
            }.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        await CloseSessionAsync(_activeSession);
        foreach (PdfSession session in _sessions)
        {
            session.Dispose();
        }
        _sessions.Clear();
        _thumbnailCache.Clear();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await PickDocumentAsync();
    private async void CloseButton_Click(object sender, RoutedEventArgs e) => await CloseDocumentAsync();
    private async void PreviousButton_Click(object sender, RoutedEventArgs e) => await NavigateToPageAsync(GetPreviousPage());
    private async void NextButton_Click(object sender, RoutedEventArgs e) => await NavigateToPageAsync(GetNextPage());

    private async void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_opening || _document is null)
        {
            return;
        }
        _rotation = (_rotation + 90) % 360;
        _pageSizes = null;
        _pageSizesRotation = -1;
        _sizesComplete = false;
        ClearPageSurfaces();
        await RenderCurrentPageAsync();
    }

    private async void ZoomSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _zoomMode = ZoomSelector.SelectedIndex;
        if (_document is not null)
        {
            await RenderCurrentPageAsync();
        }
    }

    private async void DisplayModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayModeSelector is null || _displayMode == (ReaderDisplayMode)DisplayModeSelector.SelectedIndex)
        {
            return;
        }
        _displayMode = (ReaderDisplayMode)DisplayModeSelector.SelectedIndex;
        if (_displayMode == ReaderDisplayMode.SideBySideSingle)
        {
            _page = GetPairStart(_page);
        }
        if (_document is not null && !_opening)
        {
            await RefreshLayoutAsync(true);
        }
    }

    private async void PageNumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitPageNumberAsync();
        }
    }

    private async void PageNumberBox_LostFocus(object sender, RoutedEventArgs e) => await CommitPageNumberAsync();

    private async Task CommitPageNumberAsync()
    {
        if (int.TryParse(PageNumberBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) && page != _page)
        {
            await NavigateToPageAsync(page);
        }
        else
        {
            UpdateControls();
        }
    }

    private void FindToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (SearchBar is null)
        {
            return;
        }
        bool visible = FindToggle.IsChecked == true;
        SearchBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }
        else
        {
            _searchCancellation.Cancel();
            _searchMessage = string.Empty;
            UpdateStatus();
        }
    }

    private async void TextPaneToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TextPane is null || _updatingUi)
        {
            return;
        }
        UpdateTextPaneLayout();
        if (TextPaneToggle.IsChecked == true)
        {
            await LoadPageTextAsync();
        }
    }

    private async void CopyTextButton_Click(object sender, RoutedEventArgs e)
    {
        PdfDocument? document = _document;
        if (document is null)
        {
            return;
        }
        int page = _page;
        _copyCancellation.Cancel();
        _copyCancellation.Dispose();
        _copyCancellation = new CancellationTokenSource();
        try
        {
            string text = await UseDocumentAsync(document, pdf => pdf.GetPageText(page), _copyCancellation.Token);
            if (_closed || !ReferenceEquals(document, _document))
            {
                return;
            }
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
            _searchMessage = $"Copied text from page {page}";
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Unable to copy page text", exception);
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await FindNextAsync();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchCancellation.Cancel();
        _searchMessage = string.Empty;
        if (FindNextButton is not null)
        {
            FindNextButton.IsEnabled = !string.IsNullOrWhiteSpace(SearchBox.Text);
        }
        if (StatusText is not null)
        {
            UpdateStatus();
        }
    }

    private async void FindNextButton_Click(object sender, RoutedEventArgs e) => await FindNextAsync();
    private void CloseSearchButton_Click(object sender, RoutedEventArgs e) => FindToggle.IsChecked = false;
    private void CloseTextPaneButton_Click(object sender, RoutedEventArgs e) => TextPaneToggle.IsChecked = false;

    private void PageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_document is not null && !_opening && !_closed)
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }
    }

    private void PageScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_document is not null && IsContinuousMode() && !_closed)
        {
            _viewportTimer.Stop();
            _viewportTimer.Start();
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open PDF";
            e.Handled = true;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            StorageFile? file = items.OfType<StorageFile>().FirstOrDefault(item =>
                string.Equals(item.FileType, ".pdf", StringComparison.OrdinalIgnoreCase));
            if (file is not null)
            {
                await OpenDocumentAsync(file.Path);
            }
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Unable to open dropped file", exception);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickDocumentAsync();
    }

    private async void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CloseDocumentAsync();
    }

    private void FindAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_document is not null)
        {
            FindToggle.IsChecked = true;
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }
    }

    private async void PreviousAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await NavigateToPageAsync(GetPreviousPage());
    }

    private async void NextAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await NavigateToPageAsync(GetNextPage());
    }

    private async void RotateAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_opening || _document is null)
        {
            return;
        }
        _rotation = (_rotation + 90) % 360;
        _pageSizes = null;
        _pageSizesRotation = -1;
        _sizesComplete = false;
        ClearPageSurfaces();
        await RenderCurrentPageAsync();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FindToggle.IsChecked = false;
    }

    private sealed class ThumbnailItem : INotifyPropertyChanged
    {
        public int Page { get; }
        private ImageSource? _image;
        public ImageSource? Image
        {
            get => _image;
            set
            {
                if (ReferenceEquals(_image, value))
                {
                    return;
                }
                _image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }
        public string PageLabel => $"Page {Page}";
        public event PropertyChangedEventHandler? PropertyChanged;

        public ThumbnailItem(int page, ImageSource? image)
        {
            Page = page;
            Image = image;
        }
    }

    private sealed record OutlineNodeModel(string Title, int Page);

    private sealed class PageSurface(Border container, Image image)
    {
        public Border Container { get; } = container;
        public Image Image { get; } = image;
        public WriteableBitmap? Bitmap { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
    }
}
