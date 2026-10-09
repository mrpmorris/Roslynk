using Microsoft.AspNetCore.Components;

namespace Lib;

public partial class Widget
{
	[Parameter]
	public int Count { get; set; }

	public void Refresh() => StateHasChanged();
}
