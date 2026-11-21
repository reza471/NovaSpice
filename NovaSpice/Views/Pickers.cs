using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NovaSpice.Views;

public static class Pickers
{
    static IntPtr Hwnd => WindowNative.GetWindowHandle(App.Main!);

    public static async Task<string?> OpenAsync(params string[] exts)
    {
        var p = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        InitializeWithWindow.Initialize(p, Hwnd);
        foreach (var e in exts) p.FileTypeFilter.Add(e);
        var f = await p.PickSingleFileAsync();
        return f?.Path;
    }

    public static async Task<string?> SaveAsync(string name, string label, string ext)
    {
        var p = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = name };
        InitializeWithWindow.Initialize(p, Hwnd);
        p.FileTypeChoices.Add(label, new List<string> { ext });
        var f = await p.PickSaveFileAsync();
        return f?.Path;
    }
}
