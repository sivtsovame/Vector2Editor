using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.IO;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace VectorEditor
{
    public partial class MainWindow : Window
    {
        private enum Tool { Select, Rectangle, Ellipse, Line, Polygon }
        private enum PanelKind { None, Shapes, Stroke, Fill, Thickness }

        private Tool _currentTool = Tool.Rectangle;
        private PanelKind _openedPanel = PanelKind.None;

        // Рисование
        private bool _isDrawingTwoClick = false;
        private bool _isDrawingDrag = false;
        private Point _startPoint;

        // Полигон
        private bool _isPolygonDrawing = false;
        private readonly List<Point> _polygonPoints = new();
        private Polyline? _polygonPreview;

        // Канва/превью
        private Canvas? _activeCanvas;
        private Shape? _previewShape;

        // Перемещение фигуры
        private bool _isMoving = false;
        private Shape? _movingShape;
        private Point _movePointerStart;
        private Point _moveShapeStart;
        private Point _moveLineStart;
        private Point _moveLineEnd;

        // Панорамирование
        private bool _isPanning = false;
        private bool _isSpaceDown = false;
        private Point _panStartPointer;
        private Point _panStartTranslate;

        // Выделение
        private Shape? _selectedShape;
        private IBrush? _savedStroke;
        private double _savedStrokeThickness;
        private AvaloniaList<double>? _savedDash;

        // Текущие стили
        private IBrush _currentStroke = Brushes.DarkSlateBlue;
        private IBrush _currentFill = new SolidColorBrush(Color.FromRgb(180, 205, 255));
        private double _currentStrokeThickness = 2;

        // Undo/Redo
        private const int MaxHistory = 5;
        private readonly List<ICanvasAction> _undoStack = new();
        private readonly List<ICanvasAction> _redoStack = new();

        // Zoom
        private double _zoom = 1.0;
        private const double MinZoom = 0.25;
        private const double MaxZoom = 4.0;
        private const double ZoomStep = 1.2;

        private readonly ScaleTransform _scale = new();
        private readonly TranslateTransform _translate = new();
        private readonly TransformGroup _transformGroup = new();

        // UI зума
        private TextBlock? _zoomPercent;
        private Slider? _zoomSlider;

        // Путь последнего сохранения
        private string? _lastSavedPath;

        public MainWindow()
        {
            InitializeComponent();

            // Клавиши
            this.KeyDown += OnRootKeyDown;
            this.KeyUp   += OnRootKeyUp;

            // Кнопки верхнего тулбара
            this.FindControl<Button>("OpenBtn")!.Click   += OnOpenVec;
            this.FindControl<Button>("SaveBtn")!.Click   += OnSaveVec;
            this.FindControl<Button>("SaveAsBtn")!.Click += OnSaveAsVec;

            this.FindControl<Button>("UndoBtn")!.Click    += OnUndo;
            this.FindControl<Button>("RedoBtn")!.Click    += OnRedo;

            this.FindControl<Button>("SelectBtn")!.Click  += OnSelectToolClicked;
            this.FindControl<Button>("DeleteBtn")!.Click  += OnDeleteClicked;

            this.FindControl<Button>("ShapesBtn")!.Click  += OnShowShapes;
            this.FindControl<Button>("StrokeBtn")!.Click  += OnShowStroke;
            this.FindControl<Button>("FillBtn")!.Click    += OnShowFill;
            this.FindControl<Button>("ThickBtn")!.Click   += OnShowThickness;

            this.FindControl<Button>("ZoomInBtn")!.Click  += OnZoomIn;
            this.FindControl<Button>("ZoomOutBtn")!.Click += OnZoomOut;
            this.FindControl<Button>("Zoom100Btn")!.Click += OnZoomReset;

            this.FindControl<Button>("ExportSvgBtn")!.Click += OnExportSvg;

            _zoomPercent = this.FindControl<TextBlock>("ZoomPercent");
            _zoomSlider  = this.FindControl<Slider>("ZoomSlider");

            if (_zoomSlider != null)
            {
                _zoomSlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value) && _zoomSlider.IsFocused)
                    {
                        var c = GetMainCanvas(); if (c is null) return;
                        var center = new Point(c.Bounds.Width / 2, c.Bounds.Height / 2);
                        var targetZoom = Math.Clamp((_zoomSlider.Value / 100.0), MinZoom, MaxZoom);
                        ApplyZoomAt(center, targetZoom);
                        UpdateZoomUi();
                    }
                };
            }
            UpdateZoomUi();

            var canvas = GetMainCanvas();
            if (canvas != null)
            {
                canvas.PointerPressed      += OnCanvasPointerPressed;
                canvas.PointerMoved        += OnCanvasPointerMoved;
                canvas.PointerReleased     += OnCanvasPointerReleased;
                canvas.PointerWheelChanged += OnCanvasWheel;

                _transformGroup.Children = new Transforms { _scale, _translate };
                _scale.ScaleX = _scale.ScaleY = _zoom;
                _translate.X = 0;
                _translate.Y = 0;
                canvas.RenderTransform = _transformGroup;
            }

            var host = GetHost();
            if (host != null)
                host.SizeChanged += (_, __) => ClampTransformToHost();
        }

        // ===== доступ к элементам
        private StackPanel? GetOptionsHost() => this.FindControl<StackPanel>("OptionsHost");
        private Canvas? GetMainCanvas() => this.FindControl<Canvas>("DrawCanvas");
        private Border? GetHost() => this.FindControl<Border>("CanvasHost");

        // ===== панели опций
        private void ShowPanel(PanelKind kind)
        {
            var host = GetOptionsHost();
            if (host is null) return;

            if (_openedPanel == kind)
            {
                host.Children.Clear();
                _openedPanel = PanelKind.None;
                return;
            }

            host.Children.Clear();

            switch (kind)
            {
                case PanelKind.Shapes:
                    host.Children.Add(MakeTopBtn("Прямоугольник", OnRectToolClicked));
                    host.Children.Add(MakeTopBtn("Эллипс", OnEllipseToolClicked));
                    host.Children.Add(MakeTopBtn("Линия", OnLineToolClicked));
                    host.Children.Add(MakeTopBtn("Многоугольник", OnPolygonToolClicked));
                    break;

                case PanelKind.Stroke:
                    host.Children.Add(MakeTopBtn("Синий", OnStrokeBlue));
                    host.Children.Add(MakeTopBtn("Красный", OnStrokeRed));
                    host.Children.Add(MakeTopBtn("Чёрный", OnStrokeBlack));
                    host.Children.Add(MakeTopBtn("Зелёный", OnStrokeGreen));
                    host.Children.Add(MakeTopBtn("Оранжевый", OnStrokeOrange));
                    break;

                case PanelKind.Fill:
                    host.Children.Add(MakeTopBtn("Нет", OnFillNone));
                    host.Children.Add(MakeTopBtn("Фиолет", OnFillViolet));
                    host.Children.Add(MakeTopBtn("Голубая", OnFillBlue));
                    host.Children.Add(MakeTopBtn("Жёлтая", OnFillYellow));
                    host.Children.Add(MakeTopBtn("Розовая", OnFillPink));
                    host.Children.Add(MakeTopBtn("Салатовая", OnFillGreen));
                    break;

                case PanelKind.Thickness:
                    host.Children.Add(new TextBlock
                    {
                        Text = "Толщина:",
                        Foreground = Brushes.White,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    });
                    var cb = new ComboBox
                    {
                        Width = 80,
                        Background = Brushes.White,
                        Foreground = Brushes.Black,
                        Items = { "1", "2", "4", "6" }
                    };
                    cb.SelectedIndex = _currentStrokeThickness switch
                    {
                        1 => 0, 2 => 1, 4 => 2, 6 => 3, _ => 1
                    };
                    cb.SelectionChanged += OnThicknessChangedFromCombo;
                    host.Children.Add(cb);
                    break;
            }

            _openedPanel = kind;
        }

        private Button MakeTopBtn(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> handler)
        {
            var b = new Button
            {
                Content = text,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                Margin = new Thickness(2, 0, 2, 0)
            };
            b.Click += handler;
            return b;
        }

        public void OnShowShapes(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Shapes);
        public void OnShowStroke(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Stroke);
        public void OnShowFill(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Fill);
        public void OnShowThickness(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Thickness);

        // ===== выбор инструмента
        public void OnSelectToolClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentTool = Tool.Select; ResetDrawingState(); }
        public void OnRectToolClicked  (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentTool = Tool.Rectangle; ResetDrawingState(); }
        public void OnEllipseToolClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e){ _currentTool = Tool.Ellipse;  ResetDrawingState(); }
        public void OnLineToolClicked  (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentTool = Tool.Line;     ResetDrawingState(); }
        public void OnPolygonToolClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e){ _currentTool = Tool.Polygon;  ResetDrawingState(); }

        // ===== стили
        public void OnStrokeBlue (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentStroke = Brushes.DarkSlateBlue; ApplyStrokeToSelected(); }
        public void OnStrokeRed  (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentStroke = Brushes.Crimson;       ApplyStrokeToSelected(); }
        public void OnStrokeBlack(object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentStroke = Brushes.Black;         ApplyStrokeToSelected(); }
        public void OnStrokeGreen(object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentStroke = Brushes.ForestGreen;   ApplyStrokeToSelected(); }
        public void OnStrokeOrange(object? s, Avalonia.Interactivity.RoutedEventArgs e){ _currentStroke = new SolidColorBrush(Color.FromRgb(255,140,0)); ApplyStrokeToSelected(); }

        public void OnFillNone  (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentFill = Brushes.Transparent;      ApplyFillToSelected(); }
        public void OnFillViolet(object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentFill = new SolidColorBrush(Color.FromRgb(140,110,220)); ApplyFillToSelected(); }
        public void OnFillBlue  (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentFill = new SolidColorBrush(Color.FromRgb(120,180,255)); ApplyFillToSelected(); }
        public void OnFillYellow(object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentFill = new SolidColorBrush(Color.FromRgb(255,235,80));  ApplyFillToSelected(); }
        public void OnFillPink  (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentFill = new SolidColorBrush(Color.FromRgb(255,150,200)); ApplyFillToSelected(); }
        public void OnFillGreen (object? s, Avalonia.Interactivity.RoutedEventArgs e) { _currentFill = new SolidColorBrush(Color.FromRgb(190,255,170)); ApplyFillToSelected(); }

        public void OnThicknessChangedFromCombo(object? s, SelectionChangedEventArgs e)
        {
            if (s is ComboBox cb && cb.SelectedItem is string txt && double.TryParse(txt, out double th))
            {
                _currentStrokeThickness = th;
                ApplyThicknessToSelected();
            }
        }

        private void ApplyStrokeToSelected()  { if (_selectedShape is null) return; _selectedShape.Stroke = _currentStroke; }
        private void ApplyFillToSelected()    { if (_selectedShape is null) return; if (_selectedShape is not Line) _selectedShape.Fill = _currentFill; }
        private void ApplyThicknessToSelected(){ if (_selectedShape is null) return; _selectedShape.StrokeThickness = _currentStrokeThickness; }

        // ===== Undo/Redo
        public void OnUndo(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_undoStack.Count == 0) return;
            var act = _undoStack[^1]; _undoStack.RemoveAt(_undoStack.Count - 1);
            act.Undo(); _redoStack.Add(act);
            if (_redoStack.Count > MaxHistory) _redoStack.RemoveAt(0);
        }
        public void OnRedo(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_redoStack.Count == 0) return;
            var act = _redoStack[^1]; _redoStack.RemoveAt(_redoStack.Count - 1);
            act.Redo(); _undoStack.Add(act);
            if (_undoStack.Count > MaxHistory) _undoStack.RemoveAt(0);
        }
        private void PushAction(ICanvasAction action)
        {
            _undoStack.Add(action);
            if (_undoStack.Count > MaxHistory) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        // ===== Горячие клавиши
        private void OnRootKeyDown(object? s, KeyEventArgs e)
        {
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                OnDeleteClicked(this, new Avalonia.Interactivity.RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Space) _isSpaceDown = true;

            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))      { OnZoomIn(this, new Avalonia.Interactivity.RoutedEventArgs());  e.Handled = true; return; }
            if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { OnZoomOut(this, new Avalonia.Interactivity.RoutedEventArgs()); e.Handled = true; return; }
            if (ctrl && e.Key == Key.D0)                                  { OnZoomReset(this, new Avalonia.Interactivity.RoutedEventArgs());e.Handled = true; return; }
        }
        private void OnRootKeyUp(object? s, KeyEventArgs e)
        {
            if (e.Key == Key.Space) { _isSpaceDown = false; _isPanning = false; }
        }

        // ===== Выделение/удаление
        private void SetSelectedShape(Shape? shape)
        {
            ClearSelectionVisual();
            _selectedShape = shape;
            if (shape is null) return;
            _savedStroke = shape.Stroke;
            _savedStrokeThickness = shape.StrokeThickness;
            _savedDash = shape.StrokeDashArray;
            shape.Stroke = Brushes.DimGray;
            shape.StrokeThickness = _savedStrokeThickness + 1;
            shape.StrokeDashArray = new AvaloniaList<double> { 4, 2 };
        }
        private void ClearSelectionVisual()
        {
            if (_selectedShape is null) return;
            _selectedShape.Stroke = _savedStroke;
            _selectedShape.StrokeThickness = _savedStrokeThickness;
            _selectedShape.StrokeDashArray = _savedDash;
            _selectedShape = null; _savedStroke = null; _savedDash = null;
        }
        public void OnDeleteClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var canvas = GetMainCanvas(); if (canvas is null) return;
            if (_selectedShape is not null)
            {
                var target = _selectedShape; ClearSelectionVisual();
                canvas.Children.Remove(target); PushAction(new DeleteShapeAction(canvas, target)); return;
            }
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is Shape shape) { canvas.Children.RemoveAt(i); PushAction(new DeleteShapeAction(canvas, shape)); return; }
            }
        }

        // ===== Указатели
        public void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Canvas canvas) return;
            _activeCanvas = canvas;
            var pos = e.GetPosition(canvas);

            var props = e.GetCurrentPoint(canvas).Properties;
            if (_isSpaceDown || props.IsRightButtonPressed || props.IsMiddleButtonPressed)
            {
                _isPanning = true;
                _panStartPointer = pos;
                _panStartTranslate = new Point(_translate.X, _translate.Y);
                e.Pointer.Capture(canvas);
                return;
            }

            if (_currentTool == Tool.Select)
            {
                var hit = HitTestShape(canvas, pos);
                SetSelectedShape(hit);
                if (hit is not null)
                {
                    _isMoving = true; _movingShape = hit; _movePointerStart = pos;
                    switch (hit)
                    {
                        case Rectangle rect: _moveShapeStart = new Point(Canvas.GetLeft(rect), Canvas.GetTop(rect)); break;
                        case Ellipse ell:    _moveShapeStart = new Point(Canvas.GetLeft(ell),  Canvas.GetTop(ell));  break;
                        case Line line:      _moveLineStart = line.StartPoint; _moveLineEnd = line.EndPoint;       break;
                        case Polygon poly:   _moveShapeStart = pos; break;
                    }
                    e.Pointer.Capture(canvas);
                }
                return;
            }

            if (_currentTool == Tool.Polygon)
            {
                if (!_isPolygonDrawing)
                {
                    _isPolygonDrawing = true; _polygonPoints.Clear(); _polygonPoints.Add(pos);
                    _polygonPreview = new Polyline { Stroke = Brushes.Gray, StrokeThickness = 1, Points = new AvaloniaList<Point>(_polygonPoints) };
                    canvas.Children.Add(_polygonPreview);
                }
                else
                {
                    _polygonPoints.Add(pos);
                    _polygonPreview!.Points = new AvaloniaList<Point>(_polygonPoints);
                }
                if (e.ClickCount == 2 && _polygonPoints.Count >= 3) FinishPolygon(canvas);
                return;
            }

            if (!_isDrawingTwoClick && !_isDrawingDrag)
            {
                _isDrawingTwoClick = true; _startPoint = pos; CreatePreview(canvas, pos); return;
            }
            else if (_isDrawingTwoClick)
            {
                CommitShape(pos); return;
            }

            _isDrawingDrag = true; _startPoint = pos; CreatePreview(canvas, pos); e.Pointer.Capture(canvas);
        }

        public void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_activeCanvas is null) return;
            var pos = e.GetPosition(_activeCanvas);

            if (_isPanning)
            {
                var dx = pos.X - _panStartPointer.X;
                var dy = pos.Y - _panStartPointer.Y;
                _translate.X = _panStartTranslate.X + dx;
                _translate.Y = _panStartTranslate.Y + dy;
                ClampTransformToHost();
                return;
            }

            if (_isMoving && _movingShape is not null)
            {
                var dx = pos.X - _movePointerStart.X;
                var dy = pos.Y - _movePointerStart.Y;

                switch (_movingShape)
                {
                    case Rectangle rect:
                        Canvas.SetLeft(rect, _moveShapeStart.X + dx);
                        Canvas.SetTop(rect,  _moveShapeStart.Y + dy);
                        ClampRectShapeToCanvas(rect);
                        break;

                    case Ellipse ell:
                        Canvas.SetLeft(ell, _moveShapeStart.X + dx);
                        Canvas.SetTop(ell,  _moveShapeStart.Y + dy);
                        ClampRectShapeToCanvas(ell);
                        break;

                    case Line line:
                        line.StartPoint = new Point(_moveLineStart.X + dx, _moveLineStart.Y + dy);
                        line.EndPoint   = new Point(_moveLineEnd.X   + dx, _moveLineEnd.Y   + dy);
                        ClampLineToCanvas(line);
                        break;

                    case Polygon poly:
                    {
                        double allowDxMin = double.NegativeInfinity, allowDxMax = double.PositiveInfinity;
                        double allowDyMin = double.NegativeInfinity, allowDyMax = double.PositiveInfinity;
                        var (cw, ch) = CanvasSize();
                        foreach (var p0 in poly.Points)
                        {
                            allowDxMin = Math.Max(allowDxMin, -p0.X);
                            allowDxMax = Math.Min(allowDxMax,  cw - p0.X);
                            allowDyMin = Math.Max(allowDyMin, -p0.Y);
                            allowDyMax = Math.Min(allowDyMax,  ch - p0.Y);
                        }
                        var dxC = Math.Clamp(dx, allowDxMin, allowDxMax);
                        var dyC = Math.Clamp(dy, allowDyMin, allowDyMax);
                        var newPts = new AvaloniaList<Point>();
                        foreach (var p in poly.Points) newPts.Add(new Point(p.X + dxC, p.Y + dyC));
                        poly.Points = newPts;
                        break;
                    }
                }
                return;
            }

            if (_isPolygonDrawing && _polygonPreview is not null)
            {
                var tmp = new List<Point>(_polygonPoints) { pos };
                _polygonPreview.Points = new AvaloniaList<Point>(tmp);
                return;
            }

            if ((_isDrawingTwoClick || _isDrawingDrag) && _previewShape is not null)
                UpdatePreview(pos);
        }

        public void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isPanning) { _isPanning = false; e.Pointer.Capture(null); return; }

            if (_isMoving)
            {
                if (_movingShape is Rectangle rr)      ClampRectShapeToCanvas(rr);
                else if (_movingShape is Ellipse ee)   ClampRectShapeToCanvas(ee);
                else if (_movingShape is Line ll)      ClampLineToCanvas(ll);
                _isMoving = false; _movingShape = null; e.Pointer.Capture(null); return;
            }

            if (_isDrawingDrag)
            {
                _isDrawingDrag = false; e.Pointer.Capture(null);
                if (_activeCanvas is null) return;
                var pos = e.GetPosition(_activeCanvas);
                CommitShape(pos);
            }
        }

        // Колесо/трекпад
        public void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
        {
            if (sender is not Canvas canvas) return;
            var pos = e.GetPosition(canvas);
            var factor = e.Delta.Y > 0 ? ZoomStep : (1.0 / ZoomStep);
            ApplyZoomAt(pos, _zoom * factor);
            UpdateZoomUi();
            e.Handled = true;
        }

        // ===== Полигон
        private void FinishPolygon(Canvas canvas)
        {
            if (_polygonPreview is not null) { canvas.Children.Remove(_polygonPreview); _polygonPreview = null; }
            var poly = new Polygon
            {
                Stroke = _currentStroke,
                StrokeThickness = _currentStrokeThickness,
                Fill = _currentFill,
                Points = new AvaloniaList<Point>(_polygonPoints)
            };
            canvas.Children.Add(poly);
            PushAction(new AddShapeAction(canvas, poly));
            _isPolygonDrawing = false; _polygonPoints.Clear();
        }

        // ===== Превью/коммит фигур
        private void CreatePreview(Canvas canvas, Point start)
        {
            if (_previewShape is not null) canvas.Children.Remove(_previewShape);
            switch (_currentTool)
            {
                case Tool.Rectangle:
                    var r = new Rectangle { Stroke = Brushes.Gray, StrokeThickness = 1, Fill = Brushes.Transparent };
                    canvas.Children.Add(r); _previewShape = r; PlaceRect(r, start, start);
                    break;
                case Tool.Ellipse:
                    var el = new Ellipse { Stroke = Brushes.Gray, StrokeThickness = 1, Fill = Brushes.Transparent };
                    canvas.Children.Add(el); _previewShape = el; PlaceRect(el, start, start);
                    break;
                case Tool.Line:
                    var ln = new Line { Stroke = Brushes.Gray, StrokeThickness = 1, StartPoint = start, EndPoint = start };
                    canvas.Children.Add(ln); _previewShape = ln;
                    break;
            }
        }
        // Удаление кнопок масштабирования из конструктора
        public MainWindow()
        {
            InitializeComponent();
        
            // Удаляем обработчики кнопок масштабирования
            this.FindControl<Button>("ZoomInBtn")!.Click -= OnZoomIn;
            this.FindControl<Button>("ZoomOutBtn")!.Click -= OnZoomOut;
            this.FindControl<Button>("Zoom100Btn")!.Click -= OnZoomReset;
        
            // Удаляем обработку изменения масштаба колесиком
            var canvas = GetMainCanvas();
            if (canvas != null)
            {
                canvas.PointerWheelChanged -= OnCanvasWheel;
            }
        
            // Остальной код конструктора остается без изменений
        }
        
        // Удаление метода обработки событий колесика
        public void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
        {
            // Этот метод можно удалить, так как масштабирование колесиком больше не используется
        }
        
        // Исправление сохранения заливки в методе SaveToVec
        private void SaveToVec(string path)
        {
            var canvas = GetMainCanvas() ?? throw new InvalidOperationException("Canvas not found");
            using var sw = new StreamWriter(path, false, Encoding.UTF8);
            var inv = CultureInfo.InvariantCulture;
        
            sw.WriteLine("VEC1");
            sw.WriteLine($"Zoom:{_zoom.ToString(inv)}");
            sw.WriteLine($"Count:{canvas.Children.Count}");
        
            foreach (var child in canvas.Children)
            {
                sw.WriteLine("BEGIN_SHAPE");
                switch (child)
                {
                    case Rectangle r:
                    {
                        sw.WriteLine("Type:Rectangle");
                        double x = Canvas.GetLeft(r), y = Canvas.GetTop(r);
                        sw.WriteLine($"X:{x.ToString(inv)}");
                        sw.WriteLine($"Y:{y.ToString(inv)}");
                        sw.WriteLine($"Width:{r.Width.ToString(inv)}");
                        sw.WriteLine($"Height:{r.Height.ToString(inv)}");
                        sw.WriteLine($"Fill:{BrushToString(r.Fill)}"); // Убедимся, что заливка сохраняется
                        sw.WriteLine($"Stroke:{BrushToString(r.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{r.StrokeThickness.ToString(inv)}");
                        break;
                    }
                    case Ellipse el:
                    {
                        sw.WriteLine("Type:Ellipse");
                        double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
                        sw.WriteLine($"X:{x.ToString(inv)}");
                        sw.WriteLine($"Y:{y.ToString(inv)}");
                        sw.WriteLine($"Width:{el.Width.ToString(inv)}");
                        sw.WriteLine($"Height:{el.Height.ToString(inv)}");
                        sw.WriteLine($"Fill:{BrushToString(el.Fill)}"); // Убедимся, что заливка сохраняется
                        sw.WriteLine($"Stroke:{BrushToString(el.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{el.StrokeThickness.ToString(inv)}");
                        break;
                    }
                    case Line ln:
                    {
                        sw.WriteLine("Type:Line");
                        sw.WriteLine($"X:{ln.StartPoint.X.ToString(inv)}");
                        sw.WriteLine($"Y:{ln.StartPoint.Y.ToString(inv)}");
                        sw.WriteLine($"EndX:{ln.EndPoint.X.ToString(inv)}");
                        sw.WriteLine($"EndY:{ln.EndPoint.Y.ToString(inv)}");
                        sw.WriteLine($"Fill:{BrushToString(null)}");
                        sw.WriteLine($"Stroke:{BrushToString(ln.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{ln.StrokeThickness.ToString(inv)}");
                        break;
                    }
                    case Polygon pg:
                    {
                        sw.WriteLine("Type:Polygon");
                        sw.WriteLine("X:0");
                        sw.WriteLine("Y:0");
                        sw.WriteLine("Width:0");
                        sw.WriteLine("Height:0");
                        sw.WriteLine($"Fill:{BrushToString(pg.Fill)}"); // Убедимся, что заливка сохраняется
                        sw.WriteLine($"Stroke:{BrushToString(pg.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{pg.StrokeThickness.ToString(inv)}");
                        var sb = new StringBuilder();
                        for (int i = 0; i < pg.Points.Count; i++)
                        {
                            if (i > 0) sb.Append(';');
                            sb.Append(pg.Points[i].X.ToString(inv));
                            sb.Append(',');
                            sb.Append(pg.Points[i].Y.ToString(inv));
                        }
                        sw.WriteLine($"Points:{sb}");
                        break;
                    }
                }
                sw.WriteLine("END_SHAPE");
            }
        }
        
        // Исправление загрузки заливки в методе LoadFromVec
        private void LoadFromVec(string path)
        {
            var canvas = GetMainCanvas() ?? throw new InvalidOperationException("Canvas not found");
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "VEC1")
                throw new InvalidDataException("Неверный формат файла (.vec)");
        
            canvas.Children.Clear();
        
            var inv = CultureInfo.InvariantCulture;
            int i = 1;
            double zoom = 1.0;
        
            for (; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (l.StartsWith("Zoom:", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(l.Substring(5), NumberStyles.Float, inv, out zoom);
                    continue;
                }
                if (l.StartsWith("Count:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (l == "BEGIN_SHAPE") break;
            }
        
            while (i < lines.Length)
            {
                var l = lines[i++].Trim();
                if (l != "BEGIN_SHAPE") break;
        
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (i < lines.Length)
                {
                    var line = lines[i++].Trim();
                    if (line == "END_SHAPE") break;
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();
                    dict[key] = val;
                }
        
                string type = dict.ContainsKey("Type") ? dict["Type"] : "Rectangle";
        
                if (type.Equals("Rectangle", StringComparison.OrdinalIgnoreCase))
                {
                    var r = new Rectangle
                    {
                        Width = GetDouble(dict, "Width", inv),
                        Height = GetDouble(dict, "Height", inv),
                        Fill = BrushFromString(GetString(dict, "Fill")), // Убедимся, что заливка загружается
                        Stroke = BrushFromString(GetString(dict, "Stroke")),
                        StrokeThickness = GetDouble(dict, "StrokeThickness", inv, 2)
                    };
                    Canvas.SetLeft(r, GetDouble(dict, "X", inv));
                    Canvas.SetTop(r, GetDouble(dict, "Y", inv));
                    ClampRectShapeToCanvas(r);
                    canvas.Children.Add(r);
                }
                else if (type.Equals("Ellipse", StringComparison.OrdinalIgnoreCase))
                {
                    var el = new Ellipse
                    {
                        Width = GetDouble(dict, "Width", inv),
                        Height = GetDouble(dict, "Height", inv),
                        Fill = BrushFromString(GetString(dict, "Fill")), // Убедимся, что заливка загружается
                        Stroke = BrushFromString(GetString(dict, "Stroke")),
                        StrokeThickness = GetDouble(dict, "StrokeThickness", inv, 2)
                    };
                    Canvas.SetLeft(el, GetDouble(dict, "X", inv));
                    Canvas.SetTop(el, GetDouble(dict, "Y", inv));
                    ClampRectShapeToCanvas(el);
                    canvas.Children.Add(el);
                }
                else if (type.Equals("Polygon", StringComparison.OrdinalIgnoreCase))
                {
                    var pts = new AvaloniaList<Point>();
                    if (dict.TryGetValue("Points", out var spts) && !string.IsNullOrWhiteSpace(spts))
                    {
                        var items = spts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in items)
                        {
                            var parts = p.Split(',');
                            if (parts.Length == 2
                                && double.TryParse(parts[0], NumberStyles.Float, inv, out double px)
                                && double.TryParse(parts[1], NumberStyles.Float, inv, out double py))
                            {
                                pts.Add(new Point(px, py));
                            }
                        }
                    }
        
                    var pg = new Polygon
                    {
                        Points = pts,
                        Fill = BrushFromString(GetString(dict, "Fill")), // Убедимся, что заливка загружается
                        Stroke = BrushFromString(GetString(dict, "Stroke")),
                        StrokeThickness = GetDouble(dict, "StrokeThickness", inv, 2)
                    };
                    canvas.Children.Add(pg);
                }
            }
        
            ApplyZoomAt(new Point(0, 0), zoom);
            _translate.X = 0; _translate.Y = 0;
            ClampTransformToHost();
            UpdateZoomUi();
        }:    if (_previewShape is Line l) PlaceLine(l, _startPoint, pos); break;
            }
        }
        private void CommitShape(Point pos)
        {
            if (_activeCanvas is null) return;
            if (_previewShape is not null) { _activeCanvas.Children.Remove(_previewShape); _previewShape = null; }

            Shape? finalShape = null;
            switch (_currentTool)
            {
                case Tool.Rectangle:
                    var rect = new Rectangle { Stroke = _currentStroke, StrokeThickness = _currentStrokeThickness, Fill = _currentFill };
                    PlaceRect(rect, _startPoint, pos); ClampRectShapeToCanvas(rect); _activeCanvas.Children.Add(rect); finalShape = rect; break;
                case Tool.Ellipse:
                    var ell = new Ellipse { Stroke = _currentStroke, StrokeThickness = _currentStrokeThickness, Fill = _currentFill };
                    PlaceRect(ell, _startPoint, pos); ClampRectShapeToCanvas(ell); _activeCanvas.Children.Add(ell); finalShape = ell; break;
                case Tool.Line:
                    var line = new Line { Stroke = _currentStroke, StrokeThickness = _currentStrokeThickness };
                    PlaceLine(line, _startPoint, pos); ClampLineToCanvas(line); _activeCanvas.Children.Add(line); finalShape = line; break;
            }
            if (finalShape is not null) PushAction(new AddShapeAction(_activeCanvas, finalShape));
            _isDrawingTwoClick = false; _activeCanvas = null;
        }

        // ===== Утилиты фигур/хиттест
        private void PlaceRect(Shape shape, Point p1, Point p2)
        {
            double x = Math.Min(p1.X, p2.X), y = Math.Min(p1.Y, p2.Y);
            double w = Math.Abs(p2.X - p1.X), h = Math.Abs(p2.Y - p1.Y);
            Canvas.SetLeft(shape, x); Canvas.SetTop(shape, y); shape.Width = w; shape.Height = h;
            ClampRectShapeToCanvas(shape);
        }
        private void PlaceLine(Line line, Point p1, Point p2) { line.StartPoint = p1; line.EndPoint = p2; }

        private Shape? HitTestShape(Canvas canvas, Point p)
        {
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is Shape shape)
                {
                    switch (shape)
                    {
                        case Rectangle rect:
                            { double x = Canvas.GetLeft(rect), y = Canvas.GetTop(rect), w = rect.Width, h = rect.Height;
                              if (p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h) return rect; }
                            break;
                        case Ellipse ell:
                            { double x = Canvas.GetLeft(ell), y = Canvas.GetTop(ell), w = ell.Width, h = ell.Height;
                              if (p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h) return ell; }
                            break;
                        case Line line:
                            if (IsPointNearLine(p, line.StartPoint, line.EndPoint, 5)) return line;
                            break;
                        case Polygon poly:
                            if (poly.Bounds.Contains(p)) return poly; // bbox
                            break;
                    }
                }
            }
            return null;
        }
        private bool IsPointNearLine(Point p, Point a, Point b, double tol)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            if (dx == 0 && dy == 0) return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2)) <= tol;
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy); t = Math.Max(0, Math.Min(1, t));
            double px = a.X + t * dx, py = a.Y + t * dy;
            return Math.Sqrt(Math.Pow(p.X - px, 2) + Math.Pow(p.Y - py, 2)) <= tol;
        }
        private void ResetDrawingState()
        {
            _isDrawingTwoClick = false; _isDrawingDrag = false; _isPolygonDrawing = false;
            _polygonPoints.Clear(); _previewShape = null;
        }

        private (double w, double h) CanvasSize()
        {
            var canvas = GetMainCanvas();
            var b = canvas?.Bounds ?? default;
            return (b.Width, b.Height);
        }
        private void ClampRectShapeToCanvas(Shape shape)
        {
            var (cw, ch) = CanvasSize(); if (cw <= 0 || ch <= 0) return;
            double x = Canvas.GetLeft(shape), y = Canvas.GetTop(shape), w = shape.Width, h = shape.Height;
            if (w < 0) w = 0; if (h < 0) h = 0;
            x = Math.Clamp(x, 0, Math.Max(0, cw - w)); y = Math.Clamp(y, 0, Math.Max(0, ch - h));
            if (w > cw) { w = cw; x = 0; } if (h > ch) { h = ch; y = 0; }
            Canvas.SetLeft(shape, x); Canvas.SetTop(shape, y); shape.Width = w; shape.Height = h;
        }
        private void ClampLineToCanvas(Line line)
        {
            var (cw, ch) = CanvasSize(); if (cw <= 0 || ch <= 0) return;
            var s = line.StartPoint; var e = line.EndPoint;
            s = new Point(Math.Clamp(s.X, 0, cw), Math.Clamp(s.Y, 0, ch));
            e = new Point(Math.Clamp(e.X, 0, cw), Math.Clamp(e.Y, 0, ch));
            line.StartPoint = s; line.EndPoint = e;
        }

        // ===== Зум/рамка
        private void ApplyZoomAt(Point canvasPoint, double newZoom)
        {
            newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
            double prev = _zoom;
            if (Math.Abs(newZoom - prev) < 1e-6) { ClampTransformToHost(); return; }

            _zoom = newZoom;
            _scale.ScaleX = _scale.ScaleY = _zoom;

            _translate.X += canvasPoint.X * (prev - _zoom);
            _translate.Y += canvasPoint.Y * (prev - _zoom);

            ClampTransformToHost();
        }

        private void ClampTransformToHost()
        {
            var canvas = GetMainCanvas(); var host = GetHost();
            if (canvas is null || host is null) return;
            var cb = canvas.Bounds; var hb = host.Bounds;
            if (cb.Width <= 0 || cb.Height <= 0 || hb.Width <= 0 || hb.Height <= 0) return;

            double sw = cb.Width * _zoom, sh = cb.Height * _zoom;
            double hw = hb.Width,        hh = hb.Height;

            if (sw <= hw) _translate.X = (hw - sw) / 2.0;
            else          _translate.X = Math.Clamp(_translate.X, hw - sw, 0);

            if (sh <= hh) _translate.Y = (hh - sh) / 2.0;
            else          _translate.Y = Math.Clamp(_translate.Y, hh - sh, 0);
        }

        // ===== Actions
        private interface ICanvasAction { void Undo(); void Redo(); }
        private class AddShapeAction : ICanvasAction
        {
            private readonly Canvas _canvas; private readonly Shape _shape;
            public AddShapeAction(Canvas c, Shape s) { _canvas = c; _shape = s; }
            public void Undo() => _canvas.Children.Remove(_shape);
            public void Redo() { if (!_canvas.Children.Contains(_shape)) _canvas.Children.Add(_shape); }
        }
        private class DeleteShapeAction : ICanvasAction
        {
            private readonly Canvas _canvas; private readonly Shape _shape;
            public DeleteShapeAction(Canvas c, Shape s) { _canvas = c; _shape = s; }
            public void Undo() { if (!_canvas.Children.Contains(_shape)) _canvas.Children.Add(_shape); }
            public void Redo() => _canvas.Children.Remove(_shape);
        }

        // ===== Кнопки зума
        public void OnZoomIn (object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var c = GetMainCanvas(); if (c is null) return;
            var center = new Point(c.Bounds.Width / 2, c.Bounds.Height / 2);
            ApplyZoomAt(center, _zoom * 1.2);
            UpdateZoomUi();
        }
        public void OnZoomOut(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var c = GetMainCanvas(); if (c is null) return;
            var center = new Point(c.Bounds.Width / 2, c.Bounds.Height / 2);
            ApplyZoomAt(center, _zoom / 1.2);
            UpdateZoomUi();
        }
        public void OnZoomReset(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var c = GetMainCanvas(); if (c is null) return;
            ApplyZoomAt(new Point(0, 0), 1.0);
            _translate.X = 0; _translate.Y = 0;
            ClampTransformToHost();
            UpdateZoomUi();
        }
        private void UpdateZoomUi()
        {
            if (_zoomPercent != null) _zoomPercent.Text = $"{Math.Round(_zoom * 100)}%";
            if (_zoomSlider != null)  _zoomSlider.Value = Math.Round(_zoom * 100);
        }

        // ===== Экспорт SVG (как раньше)
// ===== Экспорт SVG (фикс «p не существует»)
private async void OnExportSvg(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
{
    var canvas = GetMainCanvas(); if (canvas is null) return;
    var (cw, ch) = (canvas.Bounds.Width, canvas.Bounds.Height);
    if (cw <= 0 || ch <= 0) return;

#pragma warning disable CS0618
    var sfd = new SaveFileDialog
    {
        Title = "Экспорт в SVG",
        InitialFileName = "vector_drawing.svg",
        Filters = new List<FileDialogFilter> { new FileDialogFilter { Name = "SVG", Extensions = { "svg" } } }
    };
    var path = await sfd.ShowAsync(this);
#pragma warning restore CS0618

    if (string.IsNullOrWhiteSpace(path)) return;

    var svg = new StringBuilder();
    var inv = CultureInfo.InvariantCulture;

    svg.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
    svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{cw.ToString(inv)}\" height=\"{ch.ToString(inv)}\" viewBox=\"0 0 {cw.ToString(inv)} {ch.ToString(inv)}\">");
    svg.AppendLine($"  <rect x=\"0\" y=\"0\" width=\"{cw.ToString(inv)}\" height=\"{ch.ToString(inv)}\" fill=\"white\"/>");

    foreach (var child in canvas.Children)
    {
        if (child is Rectangle r)
        {
            double x = Canvas.GetLeft(r), y = Canvas.GetTop(r);
            double w = r.Width, h = r.Height;
            var (stroke, sw, dash) = SvgStroke(r);
            var fill = SvgFill(r);
            svg.AppendLine($"  <rect x=\"{x.ToString(inv)}\" y=\"{y.ToString(inv)}\" width=\"{w.ToString(inv)}\" height=\"{h.ToString(inv)}\" {fill}{stroke}{sw}{dash}/>");
        }
        else if (child is Ellipse el)
        {
            double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
            double w = el.Width, h = el.Height;
            double cx = x + w / 2.0, cy = y + h / 2.0, rx = w / 2.0, ry = h / 2.0;
            var (stroke, sw, dash) = SvgStroke(el);
            var fill = SvgFill(el);
            svg.AppendLine($"  <ellipse cx=\"{cx.ToString(inv)}\" cy=\"{cy.ToString(inv)}\" rx=\"{rx.ToString(inv)}\" ry=\"{ry.ToString(inv)}\" {fill}{stroke}{sw}{dash}/>");
        }
        else if (child is Line ln)
        {
            var (stroke, sw, dash) = SvgStroke(ln);
            svg.AppendLine($"  <line x1=\"{ln.StartPoint.X.ToString(inv)}\" y1=\"{ln.StartPoint.Y.ToString(inv)}\" x2=\"{ln.EndPoint.X.ToString(inv)}\" y2=\"{ln.EndPoint.Y.ToString(inv)}\" {stroke}{sw}{dash}/>");
        }
        else if (child is Polygon pg)
        {
            var (stroke, sw, dash) = SvgStroke(pg);
            var fill = SvgFill(pg);            // Ошибочный код
            private int MyMethod: int;
            
            // Исправленный код
            private int MyMethod() { return 0; }

            // ✅ фикс: корректно обходим точки многоугольника
            var pts = new StringBuilder();
            foreach (var p in pg.Points)
            {
                if (pts.Length > 0) pts.Append(' ');
                pts.Append(p.X.ToString(inv));
                pts.Append(',');
                pts.Append(p.Y.ToString(inv));
            }

            svg.AppendLine($"  <polygon points=\"{pts}\" {fill}{stroke}{sw}{dash}/>");
        }
    }

    svg.AppendLine("</svg>");

    try
    {
        await File.WriteAllTextAsync(path, svg.ToString(), Encoding.UTF8);
    }
    catch (Exception ex)
    {
        await new SimpleMessageBox("Ошибка записи файла", ex.Message).ShowDialog(this);
    }
}

        private static string ColorToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        private static string BrushToSvgPaint(IBrush? brush)
        {
            if (brush is SolidColorBrush scb) return ColorToHex(scb.Color);
            return "none";
        }
        private static (string stroke, string sw, string dash) SvgStroke(Shape s)
        {
            var stroke = $"stroke=\"{BrushToSvgPaint(s.Stroke)}\" ";
            var sw = $"stroke-width=\"{s.StrokeThickness.ToString(CultureInfo.InvariantCulture)}\" ";
            string dash = "";
            if (s.StrokeDashArray is { Count: > 0 })
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                for (int i = 0; i < s.StrokeDashArray.Count; i++) { if (i > 0) sb.Append(","); sb.Append(s.StrokeDashArray[i].ToString(inv)); }
                dash = $"stroke-dasharray=\"{sb}\" ";
            }
            return (stroke, sw, dash);
        }
        private static string SvgFill(Shape s)
        {
            if (s is Line) return "fill=\"none\" ";
            if (s.Fill is SolidColorBrush scb) return $"fill=\"{ColorToHex(scb.Color)}\" ";
            return "fill=\"none\" ";
        }

        // ====== Save/Load .vec ======
        private async void OnOpenVec(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "Открыть проект (.vec)",
                AllowMultiple = false,
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "VectorEditor (*.vec)", Extensions = { "vec" } },
                    new FileDialogFilter { Name = "All files", Extensions = { "*" } }
                }
            };
            var res = await ofd.ShowAsync(this);
            if (res is null || res.Length == 0) return;

            try
            {
                LoadFromVec(res[0]);
                _lastSavedPath = res[0];
            }
            catch (Exception ex)
            {
                await new SimpleMessageBox("Ошибка загрузки файла", ex.Message).ShowDialog(this);
            }
        }

        private async void OnSaveVec(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastSavedPath))
            {
                OnSaveAsVec(sender, e);
                return;
            }
            try
            {
                SaveToVec(_lastSavedPath!);
            }
            catch (Exception ex)
            {
                await new SimpleMessageBox("Ошибка сохранения файла", ex.Message).ShowDialog(this);
            }
        }

        private async void OnSaveAsVec(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Title = "Сохранить проект",
                InitialFileName = "project.vec",
                Filters = new List<FileDialogFilter>
                {
                    new FileDialogFilter { Name = "VectorEditor (*.vec)", Extensions = { "vec" } },
                    new FileDialogFilter { Name = "All files", Extensions = { "*" } }
                }
            };
            var path = await sfd.ShowAsync(this);
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                SaveToVec(path);
                _lastSavedPath = path;
            }
            catch (Exception ex)
            {
                await new SimpleMessageBox("Ошибка сохранения файла", ex.Message).ShowDialog(this);
            }
        }

        private void SaveToVec(string path)
        {
            var canvas = GetMainCanvas() ?? throw new InvalidOperationException("Canvas not found");
            using var sw = new StreamWriter(path, false, Encoding.UTF8);
            var inv = CultureInfo.InvariantCulture;

            sw.WriteLine("VEC1");
            sw.WriteLine($"Zoom:{_zoom.ToString(inv)}");
            sw.WriteLine($"Count:{canvas.Children.Count}");

            foreach (var child in canvas.Children)
            {
                sw.WriteLine("BEGIN_SHAPE");
                switch (child)
                {
                    case Rectangle r:
                    {
                        sw.WriteLine("Type:Rectangle");
                        double x = Canvas.GetLeft(r), y = Canvas.GetTop(r);
                        sw.WriteLine($"X:{x.ToString(inv)}");
                        sw.WriteLine($"Y:{y.ToString(inv)}");
                        sw.WriteLine($"Width:{r.Width.ToString(inv)}");
                        sw.WriteLine($"Height:{r.Height.ToString(inv)}");
                        sw.WriteLine($"Fill:{BrushToString(r.Fill)}");
                        sw.WriteLine($"Stroke:{BrushToString(r.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{r.StrokeThickness.ToString(inv)}");
                        break;
                    }
                    case Ellipse el:
                    {
                        sw.WriteLine("Type:Ellipse");
                        double x = Canvas.GetLeft(el), y = Canvas.GetTop(el);
                        sw.WriteLine($"X:{x.ToString(inv)}");
                        sw.WriteLine($"Y:{y.ToString(inv)}");
                        sw.WriteLine($"Width:{el.Width.ToString(inv)}");
                        sw.WriteLine($"Height:{el.Height.ToString(inv)}");
                        sw.WriteLine($"Fill:{BrushToString(el.Fill)}");
                        sw.WriteLine($"Stroke:{BrushToString(el.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{el.StrokeThickness.ToString(inv)}");
                        break;
                    }
                    case Line ln:
                    {
                        sw.WriteLine("Type:Line");
                        sw.WriteLine($"X:{ln.StartPoint.X.ToString(inv)}");
                        sw.WriteLine($"Y:{ln.StartPoint.Y.ToString(inv)}");
                        sw.WriteLine($"EndX:{ln.EndPoint.X.ToString(inv)}");
                        sw.WriteLine($"EndY:{ln.EndPoint.Y.ToString(inv)}");
                        sw.WriteLine($"Fill:{BrushToString(null)}");
                        sw.WriteLine($"Stroke:{BrushToString(ln.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{ln.StrokeThickness.ToString(inv)}");
                        break;
                    }
                    case Polygon pg:
                    {
                        sw.WriteLine("Type:Polygon");
                        // Для многоугольника сохраним абсолютные точки
                        sw.WriteLine("X:0");
                        sw.WriteLine("Y:0");
                        sw.WriteLine("Width:0");
                        sw.WriteLine("Height:0");
                        sw.WriteLine($"Fill:{BrushToString(pg.Fill)}");
                        sw.WriteLine($"Stroke:{BrushToString(pg.Stroke)}");
                        sw.WriteLine($"StrokeThickness:{pg.StrokeThickness.ToString(inv)}");
                        var sb = new StringBuilder();
                        for (int i = 0; i < pg.Points.Count; i++)
                        {
                            if (i > 0) sb.Append(';');
                            sb.Append(pg.Points[i].X.ToString(inv));
                            sb.Append(',');
                            sb.Append(pg.Points[i].Y.ToString(inv));
                        }
                        sw.WriteLine($"Points:{sb}");
                        break;
                    }
                }
                sw.WriteLine("END_SHAPE");
            }
        }

        private void LoadFromVec(string path)
        {
            var canvas = GetMainCanvas() ?? throw new InvalidOperationException("Canvas not found");
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "VEC1")
                throw new InvalidDataException("Неверный формат файла (.vec)");

            canvas.Children.Clear();

            var inv = CultureInfo.InvariantCulture;
            int i = 1;
            double zoom = 1.0;
            // читаем заголовок
            for (; i < lines.Length; i++)
            {
                var l = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (l.StartsWith("Zoom:", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(l.Substring(5), NumberStyles.Float, inv, out zoom);
                    continue;
                }
                if (l.StartsWith("Count:", StringComparison.OrdinalIgnoreCase))
                {
                    // можно игнорировать
                    continue;
                }
                if (l == "BEGIN_SHAPE") break;
            }

            while (i < lines.Length)
            {
                var l = lines[i++].Trim();
                if (l != "BEGIN_SHAPE") break;

                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (i < lines.Length)
                {
                    var line = lines[i++].Trim();
                    if (line == "END_SHAPE") break;
                    var idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();
                    dict[key] = val;
                }

                string type = dict.ContainsKey("Type") ? dict["Type"] : "Rectangle";

                if (type.Equals("Rectangle", StringComparison.OrdinalIgnoreCase))
                {
                    var r = new Rectangle
                    {
                        Width = GetDouble(dict, "Width", inv),
                        Height = GetDouble(dict, "Height", inv),
                        Fill = BrushFromString(GetString(dict, "Fill")),
                        Stroke = BrushFromString(GetString(dict, "Stroke")),
                        StrokeThickness = GetDouble(dict, "StrokeThickness", inv, 2)
                    };
                    Canvas.SetLeft(r, GetDouble(dict, "X", inv));
                    Canvas.SetTop(r,  GetDouble(dict, "Y", inv));
                    ClampRectShapeToCanvas(r);
                    canvas.Children.Add(r);
                }
                else if (type.Equals("Ellipse", StringComparison.OrdinalIgnoreCase))
                {
                    var el = new Ellipse
                    {
                        Width = GetDouble(dict, "Width", inv),
                        Height = GetDouble(dict, "Height", inv),
                        Fill = BrushFromString(GetString(dict, "Fill")),
                        Stroke = BrushFromString(GetString(dict, "Stroke")),
                        StrokeThickness = GetDouble(dict, "StrokeThickness", inv, 2)
                    };
                    Canvas.SetLeft(el, GetDouble(dict, "X", inv));
                    Canvas.SetTop(el,  GetDouble(dict, "Y", inv));
                    ClampRectShapeToCanvas(el);
                    canvas.Children.Add(el);
                }
                else if (type.Equals("Line", StringComparison.OrdinalIgnoreCase))
                {
                    var ln = new Line
                    {
                        StartPoint = new Point(GetDouble(dict, "X", inv),    GetDouble(dict, "Y", inv)),
                        EndPoint   = new Point(GetDouble(dict, "EndX", inv), GetDouble(dict, "EndY", inv)),
                        Stroke = BrushFromString(GetString(dict, "Stroke")),
                        StrokeThickness = GetDouble(dict, "StrokeThickness", inv, 2)
                    };
                    ClampLineToCanvas(ln);
                    canvas.Children.Add(ln);
                }
                else if (type.Equals("Polygon", StringComparison.OrdinalIgnoreCase))
                {
                    var pts = new AvaloniaList<Point>();
                    if (dict.TryGetValue("Points", out var spts) && !string.IsNullOrWhiteSpace(spts))
                    {
                        var items = spts.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in items)
                        {
                            var parts = p.Split(',');
                            if (parts.Length == 2
                                && double.TryParse(parts[0], NumberStyles.Float, inv, out double px)
                                && double.TryParse(parts[1], NumberStyles.Float, inv, out double py))
                            {
                                pts.Add(new Point(px, py));
                            }
                        }
                    }

                    var pg = new Polygon
                    {
                        Points = pts,
                        Fill = BrushFromString(GetString(dict, "Fill")),
                        Stroke = BrushFromString(GetString(dict, "Stroke")),
                        StrokeThickness = GetDouble(dict, "StrokeThickness", inv, 2)
                    };
                    // points уже абсолютные — ограничивать перемещением не нужно
                    canvas.Children.Add(pg);
                }
            }

            // применяем зум из файла
            ApplyZoomAt(new Point(0, 0), zoom);
            _translate.X = 0; _translate.Y = 0;
            ClampTransformToHost();
            UpdateZoomUi();
        }

        private static string? GetString(Dictionary<string,string> d, string key)
            => d.TryGetValue(key, out var v) ? v : null;

        private static double GetDouble(Dictionary<string,string> d, string key, IFormatProvider inv, double def = 0.0)
        {
            if (!d.TryGetValue(key, out var s)) return def;
            if (double.TryParse(s, NumberStyles.Float, inv, out var v)) return v;
            return def;
        }

        private static IBrush BrushFromString(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Brushes.Transparent;
            // Поддерживаем #ARGB или #RRGGBB (#AARRGGBB предпочтительно)
            try
            {
                // Avalonia Color.Parse понимает #AARRGGBB и #RRGGBB
                var c = Color.Parse(s);
                return new SolidColorBrush(c);
            }
            catch
            {
                return Brushes.Transparent;
            }
        }

        private static string BrushToString(IBrush? brush)
        {
            if (brush is SolidColorBrush scb)
            {
                var c = scb.Color;
                return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            return "#00000000"; // transparent
        }

        // Простое окно сообщений
        private class SimpleMessageBox : Window
        {
            public SimpleMessageBox(string title, string message)
            {
                Title = title;
                Width = 420; Height = 180;
                var btn = new Button { Content = "OK", Width = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
                btn.Click += (_, __) => Close();

                Content = new Border
                {
                    Padding = new Thickness(16),
                    Child = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) },
                            btn
                        }
                    }
                };
            }
        }
    }
}
