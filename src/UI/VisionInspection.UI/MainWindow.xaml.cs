using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using VisionInspection.Core.Models;
using VisionInspection.UI.ViewModels;

namespace VisionInspection.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void ShapeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
        {
            var viewModel = DataContext as MainViewModel;
            if (viewModel != null)
            {
                var shapeType = item.Content.ToString() == "矩形" ? ROIShapeType.Rectangle : ROIShapeType.Circle;
                viewModel.RoiEditorViewModel.CurrentShapeType = shapeType;
            }
        }
    }
}