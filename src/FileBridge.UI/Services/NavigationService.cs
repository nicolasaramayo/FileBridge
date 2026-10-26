namespace FileBridge.UI.Services;

public interface INavigationService
{
    Task GoToAsync(string route);
    Task GoToAsync(string route, IDictionary<string, object> parameters);
    Task GoBackAsync();
}

public class NavigationService : INavigationService
{
    public async Task GoToAsync(string route) => await Shell.Current.GoToAsync(route);

    // Single-use parameters: with a plain IDictionary, Shell keeps them on the page and applies them again
    // when navigating back to it (ShellNavigationManager.MergeData), which re-ran SetDevice on the file
    // browser. ShellNavigationQueryParameters are cleared once applied (ShellRouteParameters.ResetToQueryParameters).
    public async Task GoToAsync(string route, IDictionary<string, object> parameters) =>
        await Shell.Current.GoToAsync(route, new ShellNavigationQueryParameters(parameters));

    public async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
}
