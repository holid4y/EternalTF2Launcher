using System.Windows.Controls;

namespace LauncherTF2.Views;

/// <summary>
/// Inventory view — backpack grid with filters and a detail panel.
/// All logic lives in <c>BackpackViewModel</c>; the code-behind is intentionally empty.
/// </summary>
public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
    }
}
