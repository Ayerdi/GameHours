global using System.IO;
global using GameHours.Core.Abstractions;
// GameHours.Desktop intentionally enables WPF and WinForms (the latter for tray integration).
// Unqualified UserControl references in desktop views should resolve to the WPF type.
global using UserControl = System.Windows.Controls.UserControl;
