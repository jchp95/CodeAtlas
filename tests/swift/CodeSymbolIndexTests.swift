import Foundation

private func expect(_ condition: @autoclosure () -> Bool, _ message: String) {
    guard condition() else { fputs("CodeSymbolIndex test failed: \(message)\n", stderr); exit(1) }
}

let csharp = """
namespace Samples;
public sealed class CargoService
{
    public async Task<Cargo> FindCargo(string id) => await store.Find(id);
    private void UpdateStatus() { }
}
"""
let csSymbols = CodeSymbolIndex.scan(csharp, fileExtension: "cs")
expect(csSymbols.contains(where: { $0.name == "CargoService" && $0.kind == "type" && $0.line == 2 }), "C# type declaration must be indexed")
expect(csSymbols.contains(where: { $0.name == "FindCargo" && $0.kind == "method" && $0.line == 4 }), "C# method declaration must be indexed")

let typescript = """
export interface CargoResponse { id: string }
export const createCargo = async (input: CargoInput) => ({ id: input.id });
export async function getCargo(id: string) { return id; }
"""
let tsSymbols = CodeSymbolIndex.scan(typescript, fileExtension: "ts")
expect(tsSymbols.contains(where: { $0.name == "CargoResponse" && $0.kind == "type" }), "TypeScript interfaces must be indexed")
expect(tsSymbols.contains(where: { $0.name == "createCargo" && $0.kind == "function" }), "TypeScript arrow functions must be indexed")
expect(tsSymbols.contains(where: { $0.name == "getCargo" && $0.kind == "function" }), "TypeScript function declarations must be indexed")

let markdown = "# Overview\n\n## Architecture\n"
let mdSymbols = CodeSymbolIndex.scan(markdown, fileExtension: "md")
expect(mdSymbols.map(\.name) == ["Overview", "Architecture"], "Markdown headings must be indexed in order")

let json = "{\n  \"name\": \"sample\",\n  \"services\": []\n}"
let jsonSymbols = CodeSymbolIndex.scan(json, fileExtension: "json")
expect(jsonSymbols.map(\.name) == ["name", "services"], "JSON object keys must be indexed")

print("Code symbol index tests passed: C#, TypeScript, Markdown and JSON outlines.")
