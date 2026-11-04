namespace Locus.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(ServerConnection.StartPage()) { Title = "Locus" };
	}
}
