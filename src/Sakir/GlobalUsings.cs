// This project renders with GDI+ (System.Drawing) but also references WPF and
// WinForms, and all three define types with the same names - Color, Brush, Pen,
// Font, FontStyle, Rectangle, Size, Point, and even Path
// (System.Windows.Shapes.Path vs System.IO.Path).
//
// Aliasing the drawing types globally keeps the rendering code readable instead
// of fully qualifying every one of them. UI code that wants a WPF type spells it
// out (e.g. System.Windows.Media.Color).

global using System.IO;

global using Path = System.IO.Path;

global using Color = System.Drawing.Color;
global using Brush = System.Drawing.Brush;
global using Brushes = System.Drawing.Brushes;
global using Pen = System.Drawing.Pen;
global using Font = System.Drawing.Font;
global using FontStyle = System.Drawing.FontStyle;
global using Rectangle = System.Drawing.Rectangle;
global using RectangleF = System.Drawing.RectangleF;
global using Size = System.Drawing.Size;
global using Point = System.Drawing.Point;
global using PointF = System.Drawing.PointF;
global using Image = System.Drawing.Image;
global using Bitmap = System.Drawing.Bitmap;
global using Graphics = System.Drawing.Graphics;
