using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;

namespace VectorEditor
{
    public partial class MainWindow : Window
    {
        // ----- инструменты -----
        private enum Tool
        {
            Select,
            Rectangle,
            Line
        }

        private Tool _currentTool = Tool.Rectangle;

        // ----- какая панель сейчас открыта -----
        private enum PanelKind
        {
            None,
            Shapes,
            Stroke,
            Fill,
            Thickness
        }

        private PanelKind _openedPanel = PanelKind.None;

        // ----- рисование -----
        private bool _isDrawingTwoClick = false;
        private bool _isDrawingDrag = false;
        private Point _startPoint;
        private Canvas? _activeCanvas;
        private Shape? _previewShape;

        // ----- перемещение -----
        private bool _isMoving = false;
        private Shape? _movingShape;
        private Point _movePointerStart;
        private Point _moveShapeStart;
        private Point _moveLineStart;
        private Point _moveLineEnd;

        // ----- стили -----
        private IBrush _currentStroke = Brushes.DarkSlateBlue;
        private IBrush _currentFill = new SolidColorBrush(Color.FromRgb(180, 205, 255));
        private double _currentStrokeThickness = 2;

        // ----- undo/redo -----
        private const int MaxHistory = 5;
        private readonly List<ICanvasAction> _undoStack = new();
        private readonly List<ICanvasAction> _redoStack = new();

        public MainWindow()
        {
            InitializeComponent();
        }

        // ================== ПАНЕЛИ (показать / спрятать) ==================
        private StackPanel? GetOptionsHost() => this.FindControl<StackPanel>("OptionsHost");

        private void ShowPanel(PanelKind kind)
        {
            var host = GetOptionsHost();
            if (host is null) return;

            // если нажали ту же самую — просто спрячем
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
                    {
                        var b1 = MakeTopButton("Прямоугольник", OnRectToolClicked);
                        var b2 = MakeTopButton("Линия", OnLineToolClicked);
                        host.Children.Add(b1);
                        host.Children.Add(b2);
                    }
                    break;

                case PanelKind.Stroke:
                    {
                        host.Children.Add(MakeTopButton("Синий", OnStrokeBlue));
                        host.Children.Add(MakeTopButton("Красный", OnStrokeRed));
                        host.Children.Add(MakeTopButton("Чёрный", OnStrokeBlack));
                        host.Children.Add(MakeTopButton("Зелёный", OnStrokeGreen));
                        host.Children.Add(MakeTopButton("Оранжевый", OnStrokeOrange));
                    }
                    break;

                case PanelKind.Fill:
                    {
                        host.Children.Add(MakeTopButton("Нет", OnFillNone));
                        host.Children.Add(MakeTopButton("Фиолет", OnFillViolet));
                        host.Children.Add(MakeTopButton("Голубая", OnFillBlue));
                        host.Children.Add(MakeTopButton("Жёлтая", OnFillYellow));
                        host.Children.Add(MakeTopButton("Розовая", OnFillPink));
                        host.Children.Add(MakeTopButton("Салатовая", OnFillGreen));
                    }
                    break;

                case PanelKind.Thickness:
                    {
                        var text = new TextBlock
                        {
                            Text = "Толщина:",
                            Foreground = Brushes.White,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                        };
                        host.Children.Add(text);

                        var cb = new ComboBox
                        {
                            Width = 80,
                            Background = Brushes.White,
                            Foreground = Brushes.Black,
                            Items = { "1", "2", "4", "6" },
                            SelectedIndex = _currentStrokeThickness switch
                            {
                                1 => 0,
                                2 => 1,
                                4 => 2,
                                6 => 3,
                                _ => 1
                            }
                        };
                        cb.SelectionChanged += OnThicknessChangedFromCombo;
                        host.Children.Add(cb);
                    }
                    break;
            }

            _openedPanel = kind;
        }

        private Button MakeTopButton(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> handler)
        {
            var btn = new Button
            {
                Content = text,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                Margin = new Thickness(2, 0, 2, 0)
            };
            btn.Click += handler;
            return btn;
        }

        // кнопки в верхней строке
        public void OnShowShapes(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Shapes);
        public void OnShowStroke(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Stroke);
        public void OnShowFill(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Fill);
        public void OnShowThickness(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPanel(PanelKind.Thickness);

        // ================== РЕЖИМЫ ==================
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

        // ================== СТИЛИ: КОНТУР ==================
        public void OnStrokeBlue(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.DarkSlateBlue;

        public void OnStrokeRed(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.Crimson;

        public void OnStrokeBlack(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.Black;

        public void OnStrokeGreen(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = Brushes.ForestGreen;

        public void OnStrokeOrange(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentStroke = new SolidColorBrush(Color.FromRgb(255, 140, 0));

        // ================== СТИЛИ: ЗАЛИВКА ==================
        public void OnFillNone(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = Brushes.Transparent;

        public void OnFillViolet(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(140, 110, 220));

        public void OnFillBlue(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(120, 180, 255));

        public void OnFillYellow(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(255, 235, 80));

        public void OnFillPink(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(255, 150, 200));

        public void OnFillGreen(object? s, Avalonia.Interactivity.RoutedEventArgs e) =>
            _currentFill = new SolidColorBrush(Color.FromRgb(190, 255, 170));

        // ================== ТОЛЩИНА ==================
        public void OnThicknessChangedFromCombo(object? s, SelectionChangedEventArgs e)
        {
            if (s is ComboBox cb && cb.SelectedItem is string txt)
            {
                if (double.TryParse(txt, out double th))
                    _currentStrokeThickness = th;
            }
        }

        // ================== UNDO/REDO ==================
        public void OnUndo(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_undoStack.Count == 0) return;
            var action = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            action.Undo();
            _redoStack.Add(action);
            if (_redoStack.Count > MaxHistory)
                _redoStack.RemoveAt(0);
        }

        public void OnRedo(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_redoStack.Count == 0) return;
            var action = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            action.Redo();
            _undoStack.Add(action);
            if (_undoStack.Count > MaxHistory)
                _undoStack.RemoveAt(0);
        }

        // ================== ХОЛСТ ==================
        public void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Canvas canvas) return;
            _activeCanvas = canvas;
            var pos = e.GetPosition(canvas);

            // перемещение
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

                    e.Pointer.Capture(canvas);
                }
                return;
            }

            // рисование
            if (!_isDrawingTwoClick && !_isDrawingDrag)
            {
                _isDrawingTwoClick = true;
                _startPoint = pos;
                CreatePreview(canvas, pos);
                return;
            }
            else if (_isDrawingTwoClick)
            {
                CommitShape(pos);
                return;
            }

            _isDrawingDrag = true;
            _startPoint = pos;
            CreatePreview(canvas, pos);
            e.Pointer.Capture(canvas);
        }

        public void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_activeCanvas is null) return;
            var pos = e.GetPosition(_activeCanvas);

            // двигаем
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

            // обновляем превью
            if ((_isDrawingTwoClick || _isDrawingDrag) && _previewShape is not null)
            {
                UpdatePreview(pos);
            }
        }

        public void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // закончили перемещение
            if (_isMoving)
            {
                if (_activeCanvas is not null && _movingShape is not null)
                {
                    ICanvasAction? moveAction = null;

                    if (_movingShape is Rectangle rect)
                    {
                        var newX = Canvas.GetLeft(rect);
                        var newY = Canvas.GetTop(rect);
                        if (Math.Abs(newX - _moveShapeStart.X) > 0.1 ||
                            Math.Abs(newY - _moveShapeStart.Y) > 0.1)
                        {
                            moveAction = new MoveRectAction(rect, _moveShapeStart, new Point(newX, newY));
                        }
                    }
                    else if (_movingShape is Line line)
                    {
                        var newStart = line.StartPoint;
                        var newEnd = line.EndPoint;
                        if (Math.Abs(newStart.X - _moveLineStart.X) > 0.1 ||
                            Math.Abs(newStart.Y - _moveLineStart.Y) > 0.1)
                        {
                            moveAction = new MoveLineAction(line,
                                                            _moveLineStart, _moveLineEnd,
                                                            newStart, newEnd);
                        }
                    }

                    if (moveAction is not null)
                        PushAction(moveAction);
                }

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

        // ================== РИСОВАНИЕ / ПРЕВЬЮ ==================
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

            if (_previewShape is not null)
            {
                _activeCanvas.Children.Remove(_previewShape);
                _previewShape = null;
            }

            Shape? finalShape = null;

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
                    finalShape = rect;
                    break;

                case Tool.Line:
                    var line = new Line
                    {
                        Stroke = _currentStroke,
                        StrokeThickness = _currentStrokeThickness
                    };
                    PlaceLine(line, _startPoint, pos);
                    _activeCanvas.Children.Add(line);
                    finalShape = line;
                    break;
            }

            if (finalShape is not null)
                PushAction(new AddShapeAction(_activeCanvas, finalShape));

            _isDrawingTwoClick = false;
            _activeCanvas = null;
        }

        // ================== УТИЛИТЫ ==================
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

        // ================== ИСТОРИЯ ==================
        private void PushAction(ICanvasAction action)
        {
            _undoStack.Add(action);
            if (_undoStack.Count > MaxHistory)
                _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        // ====== действия ======
        private interface ICanvasAction
        {
            void Undo();
            void Redo();
        }

        private class AddShapeAction : ICanvasAction
        {
            private readonly Canvas _canvas;
            private readonly Shape _shape;

            public AddShapeAction(Canvas canvas, Shape shape)
            {
                _canvas = canvas;
                _shape = shape;
            }

            public void Undo()
            {
                _canvas.Children.Remove(_shape);
            }

            public void Redo()
            {
                if (!_canvas.Children.Contains(_shape))
                    _canvas.Children.Add(_shape);
            }
        }

        private class MoveRectAction : ICanvasAction
        {
            private readonly Rectangle _rect;
            private readonly Point _oldPos;
            private readonly Point _newPos;

            public MoveRectAction(Rectangle rect, Point oldPos, Point newPos)
            {
                _rect = rect;
                _oldPos = oldPos;
                _newPos = newPos;
            }

            public void Undo()
            {
                Canvas.SetLeft(_rect, _oldPos.X);
                Canvas.SetTop(_rect, _oldPos.Y);
            }

            public void Redo()
            {
                Canvas.SetLeft(_rect, _newPos.X);
                Canvas.SetTop(_rect, _newPos.Y);
            }
        }

        private class MoveLineAction : ICanvasAction
        {
            private readonly Line _line;
            private readonly Point _oldStart;
            private readonly Point _oldEnd;
            private readonly Point _newStart;
            private readonly Point _newEnd;

            public MoveLineAction(Line line, Point oldStart, Point oldEnd, Point newStart, Point newEnd)
            {
                _line = line;
                _oldStart = oldStart;
                _oldEnd = oldEnd;
                _newStart = newStart;
                _newEnd = newEnd;
            }

            public void Undo()
            {
                _line.StartPoint = _oldStart;
                _line.EndPoint = _oldEnd;
            }

            public void Redo()
            {
                _line.StartPoint = _newStart;
                _line.EndPoint = _newEnd;
            }
        }
    }
}
