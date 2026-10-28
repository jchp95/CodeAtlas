namespace Incomplete;

public class Worker
{
    public void Known() => Console.WriteLine("ok");

    // Newtonsoft is not referenced and not restored: this call cannot be bound.
    public string Unknown(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);

    public void CallsBoth(object value)
    {
        Known();
        _ = Unknown(value);
    }
}
