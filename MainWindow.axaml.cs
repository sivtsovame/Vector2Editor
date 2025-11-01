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
        // режимы
        private enum Tool
        {
            Select,     // двигаем уже нарисованное
            Rectangle,  // рисуем прямоугольник
            Line        // рисуем линию
        }

        private Tool _currentTool = Tool.Rectangle; // по умолчанию рисуем

        // ====== РИСОВАНИЕ ======
        private bool _isDrawingTwoClick = false; // ждём второй клик
        private bool _isDrawingDrag = false;     // тянем
        private Point _startPoint;
        private Canvas? _activeCanvas;
        private Shape? _previewShape;

        // ====== ПЕРЕМЕЩЕНИЕ (drag) ======
        private bool _isMoving = false;
        private Shape? _movingShape;
        private Point _movePointerStart;
        private Point _moveShapeStart;   // для прямоугольника
        private Point _moveLineStart;    // для линии
        private Point _moveLineEnd;

        // ====== СТИЛИ ======
        private IBrush _currentStroke = Brushes.DarkSlateBlue;
        // делаем НЕ прозрачную заливку сразу:
        private IBrush _currentFill = new SolidColorBrush(Color.FromRgb(180, 205, 255));
        private double _currentStrokeThickness = 2;

        public MainWindow()
        {
            InitializeComponent();
        }

        // ----------------- КНОПКИ РЕЖИМОВ -----------------
        public void OnSelectToolClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _currentTool = Tool.Select;
            ResetDrawingState();
        }

        public void OnRectToolClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _currentTool = Tool.Rectangle;
            ResetDrawingState();
        }

        public void OnLineToolClicked(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            _currentTool = Tool.Line;
            ResetDrawingState();
        }

        // ----------------- КНОПКИ СТИЛЕЙ -----------------
        public void OnStrokeBlue(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.DarkSlateBlue;

        public void OnStrokeRed(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.Crimson;

        public void OnStrokeBlack(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.Black;

        public void OnFillNone(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = Brushes.Transparent;

        public void OnFillViolet(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(140, 110, 220)); // плотная

        public void OnFillBlue(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(120, 180, 255)); // плотная голубая

        public void OnFillYellow(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(255, 235, 80)); // плотная жёлтая

        public void OnThicknessChanged(object? s, SelectionChangedEventArgs e)
        {
            if (s is ComboBox cb && cb.SelectedItem is ComboBoxItem item && item.Content is string text)
            {
                if (double.TryParse(text, out double th))
                    _currentStrokeThickness = th;
            }
        }

        // ----------------- ХОЛСТ: POINTER PRESSED -----------------
        public void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Canvas canvas) return;
            _activeCanvas = canvas;
            var pos = e.GetPosition(canvas);

            // 1. Режим ПЕРЕМЕЩЕНИЯ
            if (_currentTool == Tool.Select)
            {
                var hit = HitTestShape(canvas, pos);
                if (hit is not null)
                {
                    _isMoving = true;
                    _movingShape = hit;
                    _movePointerStart = pos;

                    if (hit is Rectangle rect)
                    {
                        _moveShapeStart = new Point(Canvas.GetLeft(rect), Canvas.GetTop(rect));
                    }
                    else if (hit is Line line)
                    {
                        _moveLineStart = line.StartPoint;
                        _moveLineEnd = line.EndPoint;
                    }

                    // захватываем указатель, чтобы тачпад тоже тянул
                    e.Pointer.Capture(canvas);
                }
                return;
            }

            // 2. Режим РИСОВАНИЯ (rectangle / line)
            if (!_isDrawingTwoClick && !_isDrawingDrag)
            {
                // первый клик
                _isDrawingTwoClick = true;
                _startPoint = pos;
                CreatePreview(canvas, pos);
                return;
            }
            else if (_isDrawingTwoClick)
            {
                // второй клик — зафиксировать
                CommitShape(pos);
                return;
            }

            // drag-рисование (если получится удержать)
            _isDrawingDrag = true;
            _startPoint = pos;
            CreatePreview(canvas, pos);
            e.Pointer.Capture(canvas);
        }

        // ----------------- ХОЛСТ: POINTER MOVED -----------------
        public void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_activeCanvas is null) return;
            var pos = e.GetPosition(_activeCanvas);

            // перетаскивание фигуры
            if (_isMoving && _movingShape is not null)
            {
                var dx = pos.X - _movePointerStart.X;
                var dy = pos.Y - _movePointerStart.Y;

                if (_movingShape is Rectangle rect)
                {
                    Canvas.SetLeft(rect, _moveShapeStart.X + dx);
                    Canvas.SetTop(rect, _moveShapeStart.Y + dy);
                }
                else if (_movingShape is Line line)
                {
                    line.StartPoint = new Point(_moveLineStart.X + dx, _moveLineStart.Y + dy);
                    line.EndPoint = new Point(_moveLineEnd.X + dx, _moveLineEnd.Y + dy);
                }

                return;
            }

            // обновление превью при рисовании
            if ((_isDrawingTwoClick || _isDrawingDrag) && _previewShape is not null)
            {
                UpdatePreview(pos);
            }
        }

        // ----------------- ХОЛСТ: POINTER RELEASED -----------------
        public void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // закончили перетаскивание
            if (_isMoving)
            {
                _isMoving = false;
                _movingShape = null;
                e.Pointer.Capture(null);
                return;
            }

            // закончили drag-рисование
            if (_isDrawingDrag)
            {
                _isDrawingDrag = false;
                e.Pointer.Capture(null);

                if (_activeCanvas is null) return;
                var pos = e.GetPosition(_activeCanvas);
                CommitShape(pos);
            }
        }

        // ----------------- ПРЕВЬЮ / РИСОВАНИЕ -----------------
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

            _isDrawingTwoClick = false;
            _activeCanvas = null;
        }

        // ----------------- УТИЛИТЫ -----------------
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

        private Shape? HitTestShape(Canvas canvas, Point p)
        {
            // с конца — сверху
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
            {
                if (canvas.Children[i] is Shape shape)
                {
                    if (shape is Rectangle rect)
                    {
                        double x = Canvas.GetLeft(rect);
                        double y = Canvas.GetTop(rect);
                        double w = rect.Width;
                        double h = rect.Height;
                        if (p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h)
                            return rect;
                    }
                    else if (shape is Line line)
                    {
                        if (IsPointNearLine(p, line.StartPoint, line.EndPoint, 5))
                            return line;
                    }
                }
            }
            return null;
        }

        private bool IsPointNearLine(Point p, Point a, Point b, double tol)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            if (dx == 0 && dy == 0)
                return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2)) <= tol;

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
            t = Math.Max(0, Math.Min(1, t));
            double projX = a.X + t * dx;
            double projY = a.Y + t * dy;
            double dist = Math.Sqrt(Math.Pow(p.X - projX, 2) + Math.Pow(p.Y - projY, 2));
            return dist <= tol;
        }

        private void ResetDrawingState()
        {
            _isDrawingTwoClick = false;
            _isDrawingDrag = false;
            _isMoving = false;
            _previewShape = null;
            _movingShape = null;
            _activeCanvas = null;
        }
    }
}
