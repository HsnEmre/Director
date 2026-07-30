using System.Collections;
using System.ComponentModel;

namespace Director.Helpers;

public abstract class ValidatableObservableObject : ObservableObject, INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return _errors.SelectMany(pair => pair.Value).ToList();
        }

        return _errors.TryGetValue(propertyName, out var errors)
            ? errors
            : Enumerable.Empty<string>();
    }

    protected string GetFirstError(string propertyName)
    {
        return _errors.TryGetValue(propertyName, out var errors) && errors.Count > 0
            ? errors[0]
            : string.Empty;
    }

    protected void SetErrors(string propertyName, IEnumerable<string> errors)
    {
        var errorList = errors.Where(error => !string.IsNullOrWhiteSpace(error)).Distinct().ToList();

        if (errorList.Count == 0)
        {
            if (_errors.Remove(propertyName))
            {
                RaiseErrorsChanged(propertyName);
            }

            return;
        }

        _errors[propertyName] = errorList;
        RaiseErrorsChanged(propertyName);
    }

    protected void ClearErrors()
    {
        var properties = _errors.Keys.ToList();
        _errors.Clear();

        foreach (var property in properties)
        {
            RaiseErrorsChanged(property);
        }
    }

    private void RaiseErrorsChanged(string propertyName)
    {
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }
}
