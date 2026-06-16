namespace MirrorsEdgeTweaks.Services
{
    public interface IFolderPickerService
    {
        // Shows a folder browse dialog and returns the chosen path, or null if cancelled.
        string? PickFolder(string description, string? initialPath);
    }

    // Wraps the WinForms FolderBrowserDialog so view models can request a folder selection without
    // referencing the View / WinForms layer directly.
    public class FolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string description, string? initialPath)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog();
            dialog.Description = description;
            dialog.UseDescriptionForTitle = true;

            if (!string.IsNullOrEmpty(initialPath))
            {
                dialog.SelectedPath = initialPath;
            }

            System.Windows.Forms.DialogResult result = dialog.ShowDialog();
            return result == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                ? dialog.SelectedPath
                : null;
        }
    }
}
