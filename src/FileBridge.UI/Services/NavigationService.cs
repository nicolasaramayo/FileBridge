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
    public async Task GoToAsync(string route, IDictionary<string, object> parameters) => await Shell.Current.GoToAsync(route, parameters);
    public async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
}
