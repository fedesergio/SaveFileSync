// WPF projects do not import System.IO implicitly (System.Windows.Shapes.Path would clash).
global using System.IO;

// WPF-UI defines its own MessageBox types: the app uses the standard WPF ones.
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
