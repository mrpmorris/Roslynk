namespace BindLib;

public static class DialogDefaults
{
	public static Dialog.ViewModel Create() => new() { InstallDate = DateTime.Today };
}
