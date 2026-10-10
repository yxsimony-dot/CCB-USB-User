namespace UsbWebLauncher.Core.Configuration;

public sealed record ProfileValidationError(string Field, string Message);

public sealed record ProfileValidationResult(IReadOnlyList<ProfileValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
