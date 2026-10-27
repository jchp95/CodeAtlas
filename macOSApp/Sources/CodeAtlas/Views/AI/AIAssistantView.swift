import SwiftUI

struct AIAssistantView: View {
    @ObservedObject var service: AIAssistantService
    let snapshot: ArchitectureSnapshot?
    let onOpenSettings: () -> Void

    @State private var question = "Explícame la arquitectura actual, sus principales flujos y los riesgos que debería revisar antes de un refactor."
    @State private var answer = ""
    @State private var isSending = false
    @State private var localError: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Asistente de ingeniería").font(.largeTitle.bold())
                        Text("Pregunta por la arquitectura, los flujos, el impacto de cambios o los próximos tests.").font(.callout).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button(action: onOpenSettings) { Label("Configurar IA", systemImage: "slider.horizontal.3") }.buttonStyle(.bordered)
                }
                if snapshot == nil {
                    ContentUnavailableView("Carga un workspace", systemImage: "folder.badge.questionmark", description: Text("El asistente puede usar el resumen estructurado después de analizar al menos un repositorio."))
                        .frame(minHeight: 170)
                } else {
                    HStack(spacing: 8) {
                        Label("\(snapshot?.projects.count ?? 0) bloques", systemImage: "square.grid.2x2")
                        Label("\(snapshot?.relations.count ?? 0) relaciones", systemImage: "arrow.left.arrow.right")
                        Label("\((snapshot?.businessFlows ?? []).count) flujos", systemImage: "point.topleft.down.curvedto.point.bottomright.up")
                        Spacer()
                        Text(service.provider.rawValue).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    }.font(.caption)
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Tu consulta").font(.headline)
                        TextEditor(text: $question).font(.body).frame(minHeight: 115).padding(6)
                            .background(Color(nsColor: .textBackgroundColor), in: RoundedRectangle(cornerRadius: 10))
                            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
                        LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], alignment: .leading, spacing: 8) {
                            quickPrompt("Explicar la arquitectura", prompt: "Explica la arquitectura actual por responsabilidades y capas. Distingue lo confirmado del código de las inferencias.")
                            quickPrompt("Impacto de cambios", prompt: "Analiza los cambios del último snapshot. ¿Qué servicios, contratos y flujos de negocio podrían verse afectados? Cita la evidencia del resumen y señala qué necesita confirmación.")
                            quickPrompt("Riesgos arquitectónicos", prompt: "Busca riesgos arquitectónicos visibles en el snapshot: dependencias circulares posibles, contratos incompatibles, acoplamiento y relaciones ambiguas. No afirmes problemas sin evidencia.")
                            quickPrompt("Pruebas recomendadas", prompt: "Propón una estrategia de pruebas para los flujos detectados: unitarias, integración HTTP, mensajería y end-to-end. Prioriza por riesgo y explica qué evidencia falta.")
                        }
                        Toggle("Adjuntar resumen de arquitectura", isOn: $service.includeArchitectureSummary)
                            .font(.callout)
                        HStack {
                            Text(service.provider == .ollama ? "Procesamiento local (si el endpoint es local)." : "La consulta y el contexto seleccionado se enviarán a Anthropic.")
                                .font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Button {
                                Task { await sendQuestion() }
                            } label: {
                                if isSending { ProgressView().controlSize(.small); Text("Consultando…") }
                                else { Label("Preguntar", systemImage: "paperplane.fill") }
                            }.buttonStyle(.borderedProminent).disabled(isSending || question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                        }
                    }.padding(16).atlasGlassCard(cornerRadius: 13)
                }
                if let localError {
                    Label(localError, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout)
                        .padding(12).background(.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
                }
                if !answer.isEmpty {
                    VStack(alignment: .leading, spacing: 12) {
                        HStack { Text("Respuesta").font(.title3.bold()); Spacer(); Button("Copiar") { NSPasteboard.general.setString(answer, forType: .string) }.buttonStyle(.borderless) }
                        Text(answer).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                    }.padding(16).atlasGlassCard(cornerRadius: 13)
                }
            }.padding(26).frame(maxWidth: 1100, alignment: .leading)
             .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
    }

    private func quickPrompt(_ title: String, prompt: String) -> some View {
        Button { question = prompt } label: {
            HStack(spacing: 7) {
                Image(systemName: "sparkles").font(.caption)
                Text(title).font(.caption.weight(.medium))
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
    }

    @MainActor private func sendQuestion() async {
        isSending = true
        localError = nil
        defer { isSending = false }
        do {
            let context = service.includeArchitectureSummary ? AIArchitectureContext.summary(snapshot) : ""
            answer = try await service.ask(question: question, architectureContext: context)
        } catch {
            localError = error.localizedDescription
        }
    }
}
