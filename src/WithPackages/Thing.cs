using Newtonsoft.Json;

namespace WithPackages;

public sealed class Thing
{
    // CONTROL FINDING. Qodana should report this unused private field.
    // If the SARIF contains no findings at all, the analysis did not run and
    // the security result proves nothing. See "Control" under Verification.
    private readonly int _unusedField = 42;

    public string Serialize(object value) => JsonConvert.SerializeObject(value);
}
