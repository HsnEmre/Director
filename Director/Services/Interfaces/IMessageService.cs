namespace Director.Services.Interfaces;

public interface IMessageService
{
    void ShowInfo(string message, string title = "Director");

    void ShowError(string message, string title = "Director");

    bool Confirm(string message, string title = "Director");
}
