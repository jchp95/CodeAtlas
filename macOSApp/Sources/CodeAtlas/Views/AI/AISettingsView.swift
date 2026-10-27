import SwiftUI

struct AISettingsView: View {
    @ObservedObject var service: AIAssistantService

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                header
                providerSelector
                if service.provider == .ollama { ollamaSettings } else { anthropicSettings }
                dataPolicyCard
                HStack(spacing: 10) {
                    Button { service.saveSettings() } label: { Label("Guardar configuración", systemImage: "checkmark") }
                        .buttonStyle(.borderedProminent)
                    Button {
                        service.saveSettings()
                        Task { await service.testConnection() }
                    } label: {
                        if service.isCheckingConnection { ProgressView().controlSize(.small); Text("Comprobando…") }
                        else { Label("Probar conexión", systemImage: "bolt.horizontal.circle") }
                    }.buttonStyle(.bordered).disabled(service.isCheckingConnection)
                    Spacer()
                    Label(service.connectionStatus, systemImage: service.connectionStatus.contains("disponible") || service.connectionStatus.contains("conectado") ? "checkmark.circle.fill" : "info.circle")
                        .font(.caption).foregroundStyle(service.connectionStatus == "No conectado" ? .orange : .secondary)
                }
                if let error = service.lastProviderError {
                    Label(error, systemImage: "exclamationmark.triangle.fill").font(.callout).foregroundStyle(.orange)
                        .padding(12).frame(maxWidth: .infinity, alignment: .leading).background(.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
                }
            }.padding(26).frame(maxWidth: 1100, alignment: .leading)
             .frame(maxWidth: .infinity, alignment: .topLeading)
        }
        .background(.clear)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 12) {
                Image(systemName: "brain.head.profile").font(.system(size: 27)).foregroundStyle(Color.accentColor)
                    .frame(width: 56, height: 56).background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 15))
                VStack(alignment: .leading, spacing: 3) {
                    Text("Inteligencia artificial").font(.largeTitle.bold())
                    Text("Asistente de arquitectura · análisis · diagnóstico · documentación").font(.callout).foregroundStyle(.secondary)
                }
            }
            Text("La IA es opcional. El análisis estructural de CodeAtlas sigue funcionando sin proveedor configurado.")
                .font(.callout).foregroundStyle(.secondary)
        }
    }

    private var providerSelector: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Proveedor activo").font(.headline)
            Picker("Proveedor", selection: $service.provider) {
                ForEach(AIProvider.allCases) { provider in Text(provider.rawValue).tag(provider) }
            }.pickerStyle(.segmented).frame(maxWidth: 450)
            HStack(spacing: 8) {
                Image(systemName: service.provider == .ollama ? "desktopcomputer" : "cloud.fill")
                Text(service.provider == .ollama ? "Las consultas se envían a tu instancia local." : "Las consultas que envíes se transmiten a Anthropic usando su API.")
            }.font(.caption).foregroundStyle(.secondary)
        }.padding(16).atlasGlassCard(cornerRadius: 13)
    }

    private var ollamaSettings: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Ollama local").font(.title3.bold())
            LabeledContent("URL base") { TextField("http://localhost:11434", text: $service.ollamaBaseURL).textFieldStyle(.roundedBorder).frame(maxWidth: 360) }
            HStack {
                LabeledContent("Modelo local") {
                    if service.availableOllamaModels.isEmpty {
                        TextField("qwen2.5-coder:7b", text: $service.ollamaModel).textFieldStyle(.roundedBorder).frame(maxWidth: 360)
                    } else {
                        Picker("Modelo", selection: $service.ollamaModel) {
                            if service.availableOllamaModels.contains(service.ollamaModel) == false, !service.ollamaModel.isEmpty {
                                Text(service.ollamaModel).tag(service.ollamaModel)
                            }
                            ForEach(service.availableOllamaModels, id: \.self) { Text($0).tag($0) }
                        }.frame(maxWidth: 360)
                    }
                }
                Button("Descubrir modelos") { Task { await service.testConnection() } }.buttonStyle(.bordered)
            }
            Text("Requiere Ollama activo. Endpoint estándar: GET /api/tags y POST /api/chat. No necesita API key para modelos locales.")
                .font(.caption).foregroundStyle(.secondary)
        }.padding(16).atlasGlassCard(cornerRadius: 13)
    }

    private var anthropicSettings: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Anthropic API").font(.title3.bold())
            LabeledContent("Model ID") {
                if service.availableAnthropicModels.isEmpty {
                    TextField("Identificador de modelo API", text: $service.anthropicModel).textFieldStyle(.roundedBorder).frame(maxWidth: 360)
                } else {
                    Picker("Model ID", selection: $service.anthropicModel) {
                        if !service.availableAnthropicModels.contains(service.anthropicModel), !service.anthropicModel.isEmpty {
                            Text(service.anthropicModel).tag(service.anthropicModel)
                        }
                        ForEach(service.availableAnthropicModels, id: \.self) { Text($0).tag($0) }
                    }.frame(maxWidth: 360)
                }
            }
            LabeledContent("API key") {
                SecureField(service.hasSavedAnthropicKey ? "Clave guardada; escribe para sustituirla" : "sk-ant-…", text: $service.anthropicKeyDraft)
                    .textFieldStyle(.roundedBorder).frame(maxWidth: 360)
            }
            HStack {
                Label(service.hasSavedAnthropicKey ? "Clave guardada en el llavero de macOS" : "No hay clave guardada", systemImage: service.hasSavedAnthropicKey ? "key.fill" : "key")
                    .font(.caption).foregroundStyle(.secondary)
                Spacer()
                if service.hasSavedAnthropicKey {
                    Button("Eliminar clave", role: .destructive) { service.removeAnthropicKey() }.buttonStyle(.borderless)
                }
            }
            Text("La clave no se guarda en el proyecto ni en UserDefaults: se almacena en Keychain. Probar conexión consulta el catálogo de modelos; enviar una pregunta usa créditos API si tu organización los tiene habilitados.")
                .font(.caption).foregroundStyle(.secondary)
        }.padding(16).atlasGlassCard(cornerRadius: 13)
    }

    private var dataPolicyCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Control de datos", systemImage: "hand.raised.square").font(.headline)
            Toggle("Incluir resumen estructurado de arquitectura en las consultas", isOn: $service.includeArchitectureSummary)
            Text("Por defecto solo se envían nombres de proyectos, tipos, relaciones, endpoints detectados y cambios resumidos. No se adjunta código fuente, archivos .http ni variables de entorno automáticamente. En Anthropic, el resumen sale de tu equipo; con Ollama local, se envía a tu servidor configurado.")
                .font(.caption).foregroundStyle(.secondary)
        }.padding(16).atlasGlassCard(cornerRadius: 13)
    }
}

// MARK: - AI assistant screen
