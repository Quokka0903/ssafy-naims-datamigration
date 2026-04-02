using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DBImplant_N1toN2.Infrastructure.Behaviors
{
    /// <summary>
    /// TreeView SelectedItem을 ViewModel에 바인딩하는 Attached Behavior.
    /// TreeView.SelectedItem은 읽기 전용이므로, 선택 변경 시 Command를 실행하는 방식으로 MVVM 패턴을 유지한다.
    /// 사용법: behaviors:TreeViewBehavior.SelectedItemChangedCommand="{Binding OnNodeSelectedCommand}"
    /// </summary>
    public static class TreeViewBehavior
    {
        public static readonly DependencyProperty SelectedItemChangedCommandProperty =
            DependencyProperty.RegisterAttached(
                "SelectedItemChangedCommand",
                typeof(ICommand),
                typeof(TreeViewBehavior),
                new PropertyMetadata(null, OnSelectedItemChangedCommandChanged));

        public static ICommand GetSelectedItemChangedCommand(DependencyObject obj)
            => (ICommand)obj.GetValue(SelectedItemChangedCommandProperty);

        public static void SetSelectedItemChangedCommand(DependencyObject obj, ICommand value)
            => obj.SetValue(SelectedItemChangedCommandProperty, value);

        private static void OnSelectedItemChangedCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TreeView treeView) return;

            treeView.SelectedItemChanged -= OnTreeViewSelectedItemChanged;
            if (e.NewValue is ICommand)
                treeView.SelectedItemChanged += OnTreeViewSelectedItemChanged;
        }

        private static void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (sender is not TreeView treeView) return;
            var command = GetSelectedItemChangedCommand(treeView);
            if (command?.CanExecute(e.NewValue) == true)
                command.Execute(e.NewValue);
        }
    }
}
