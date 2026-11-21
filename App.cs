using Microsoft.Maui.Controls;

namespace MauiInteractionsRepro;

public class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
		=> new(new NavigationPage(new ProbePage()));
}
