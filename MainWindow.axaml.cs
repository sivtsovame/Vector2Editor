using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace VectorEditor
{
    public partial class MainWindow : Window
    {
        private enum Tool { Rectangle, Line }
        private Tool _currentTool = Tool.Rectangle;

        // состояние рисования
        private bool _isDragging = false;
        private bool _hasStartClick = false;
        private Point _startPoint;
        private Canvas? _activeCanvas;
        private Shape? _previewShape;

        // текущие стили
        private IBrush _currentStroke = Brushes.DarkSlateBlue;
        private IBrush _currentFill = new SolidColorBrush(Color.FromArgb(60, 72, 61, 139));
        private double _currentStrokeThickness = 2;

        public MainWindow()
        {
            InitializeComponent();
        }

        // ===== инструменты =====
        public void OnRectToolClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _currentTool = Tool.Rectangle;
            ResetState();
        }

        public void OnLineToolClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _currentTool = Tool.Line;
            ResetState();
        }

        // ===== выбор цвета контура =====
        public void OnStrokeBlue(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.DarkSlateBlue;

        public void OnStrokeRed(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.Crimson;

        public void OnStrokeBlack(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.Black;

        // ===== выбор заливки =====
        public void OnFillNone(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = Brushes.Transparent;

        public void OnFillViolet(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromArgb(80, 123, 78, 231));
        public void OnFillBlue(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromArgb(100, 100, 180, 255)); // голубой

        public void OnFillYellow(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromArgb(120, 255, 235, 80)); // жёлтый

        // ===== выбор толщины =====
        public void OnThicknessChanged(object? s, SelectionChangedEventArgs e)
        {
            if (s is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Content is string text)
            {
                if (double.TryParse(text, out double th))
                    _currentStrokeThickness = th;
            }
        }

        // ===== холст =====
        public void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Canvas canvas) return;
            _activeCanvas = canvas;
            var pos = e.GetPosition(canvas);

            // режим 2 клика
            if (!_hasStartClick && !_isDragging)
            {
                _hasStartClick = true;
                _startPoint = pos;
                CreatePreview(canvas, pos);
                return;
            }
            else if (_hasStartClick && !_isDragging)
            {
                CommitShape(pos);
                return;
            }

            // drag
            _isDragging = true;
            _startPoint = pos;
            CreatePreview(canvas, pos);
            e.Pointer.Capture(canvas);
        }

        public void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_activeCanvas is null) return;
            if (!_isDragging && !_hasStartClick) return;

            var pos = e.GetPosition(_activeCanvas);
            UpdatePreview(pos);
        }

        public void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isDragging) return;

            _isDragging = false;
            e.Pointer.Capture(null);

            if (_activeCanvas is null) return;
            var pos = e.GetPosition(_activeCanvas);
            CommitShape(pos);
        }

        // ============= helpers =============

        private void CreatePreview(Canvas canvas, Point start)
        {
            if (_previewShape is not null)
                canvas.Children.Remove(_previewShape);

            switch (_currentTool)
            {
                case Tool.Rectangle:
                    var preRect = new Rectangle
                    {
                        Stroke = Brushes.Gray,
                        StrokeThickness = 1,
                        Fill = Brushes.Transparent
                    };
                    canvas.Children.Add(preRect);
                    _previewShape = preRect;
                    PlaceRect(preRect, start, start);
                    break;

                case Tool.Line:
                    var preLine = new Line
                    {
                        Stroke = Brushes.Gray,
                        StrokeThickness = 1,
                        StartPoint = start,
                        EndPoint = start
                    };
                    canvas.Children.Add(preLine);
                    _previewShape = preLine;
                    break;
            }
        }

        private void UpdatePreview(Point pos)
        {
            if (_previewShape is null) return;

            switch (_currentTool)
            {
                case Tool.Rectangle:
                    if (_previewShape is Rectangle r)
                        PlaceRect(r, _startPoint, pos);
                    break;
                case Tool.Line:
                    if (_previewShape is Line l)
                        PlaceLine(l, _startPoint, pos);
                    break;
            }
        }

        private void CommitShape(Point pos)
        {
            if (_activeCanvas is null) return;

            // убрать превью
            if (_previewShape is not null)
            {
                _activeCanvas.Children.Remove(_previewShape);
                _previewShape = null;
            }

            switch (_currentTool)
            {
                case Tool.Rectangle:
                    var rect = new Rectangle
                    {
                        Stroke = _currentStroke,
                        StrokeThickness = _currentStrokeThickness,
                        Fill = _currentFill
                    };
                    PlaceRect(rect, _startPoint, pos);
                    _activeCanvas.Children.Add(rect);
                    break;

                case Tool.Line:
                    var line = new Line
                    {
                        Stroke = _currentStroke,
                        StrokeThickness = _currentStrokeThickness
                    };
                    PlaceLine(line, _startPoint, pos);
                    _activeCanvas.Children.Add(line);
                    break;
            }

            _hasStartClick = false;
            _activeCanvas = null;
        }

        private void PlaceRect(Rectangle rect, Point p1, Point p2)
        {
            double x = Math.Min(p1.X, p2.X);
            double y = Math.Min(p1.Y, p2.Y);
            double w = Math.Abs(p2.X - p1.X);
            double h = Math.Abs(p2.Y - p1.Y);

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = w;
            rect.Height = h;
        }

        private void PlaceLine(Line line, Point p1, Point p2)
        {
            line.StartPoint = p1;
            line.EndPoint = p2;
        }

        private void ResetState()
        {
            _isDragging = false;
            _hasStartClick = false;
            _activeCanvas = null;
            _previewShape = null;
        }
    }
}
