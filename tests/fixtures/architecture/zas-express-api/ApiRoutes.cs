namespace Zas.Express;

// Constants intentionally live in a separate file to verify cross-file route resolution.
public static class ApiRoutes
{
    public const string V1 = "/api/v1";
    public const string Cargo = "/cargo";
    public const string ById = "/{cargoId}";
}
