namespace GameHours.Core.Discovery;

public interface IExecutableMappingValidationPolicy
{
    bool IsHelperExecutable(string executablePath);
}
