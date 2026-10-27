import SwiftUI
import AppKit

struct EndpointHTTPExplorerView: View {
    let repositoryURL: URL?
    let catalog: HTTPTestingCatalog?
    let isLoading: Bool
    let onReload: () -> Void
    let onDirectRun: (String, String, String, String) async throws -> HTTPExecutionResult
    let onFileRun: (HTTPTestRequest, String, String, String, String, String) async throws -> HTTPExecutionResult

    @State var searchText = ""
    @State var selectedID: String?
    @State var method = "GET"
    @State var requestURL = ""
    @State var headersText = "Accept: application/json"
    @State var bodyText = ""
    @State var selectedEnvironment = ""
    @State var isExecuting = false
    @State var result: HTTPExecutionResult?
    @State var localError: String?

    var selectedEndpoint: HTTPApiEndpoint? {
        guard let selectedID, selectedID.hasPrefix("endpoint:") else { return nil }
        let id = String(selectedID.dropFirst("endpoint:".count))
        return catalog?.endpoints.first(where: { $0.id == id })
    }

    var selectedRequest: HTTPTestRequest? {
        guard let selectedID, selectedID.hasPrefix("request:") else { return nil }
        let id = String(selectedID.dropFirst("request:".count))
        return catalog?.requests.first(where: { $0.id == id })
    }

    var selectedHTTPCall: HTTPCallTrace? {
        guard let selectedID, selectedID.hasPrefix("call:") else { return nil }
        let id = String(selectedID.dropFirst("call:".count))
        return catalog?.httpCalls.first(where: { $0.id == id })
    }

    var filteredCalls: [HTTPCallTrace] {
        guard !searchText.isEmpty else { return catalog?.httpCalls ?? [] }
        return (catalog?.httpCalls ?? []).filter { call in
            [call.repositoryPath, call.sourceProjectName, call.sourcePath, call.symbol, call.method, call.route,
             call.detail, call.confidence]
                .contains { $0.localizedCaseInsensitiveContains(searchText) }
            || (catalog?.endpoints ?? []).filter { call.targetEndpointIds.contains($0.id) }
                .contains { "\($0.projectName) \($0.route)".localizedCaseInsensitiveContains(searchText) }
        }
    }

    var filteredRequests: [HTTPTestRequest] {
        guard !searchText.isEmpty else { return catalog?.requests ?? [] }
        return (catalog?.requests ?? []).filter { request in
            [request.repositoryPath, request.projectName, request.sourcePath, request.name, request.method, request.url]
                .contains { $0.localizedCaseInsensitiveContains(searchText) }
        }
    }

    var filteredEndpoints: [HTTPApiEndpoint] {
        guard !searchText.isEmpty else { return catalog?.endpoints ?? [] }
        return (catalog?.endpoints ?? []).filter { endpoint in
            [endpoint.repositoryPath, endpoint.projectName, endpoint.framework, endpoint.method, endpoint.route,
             endpoint.sourcePath, endpoint.requestType ?? ""]
                .contains { $0.localizedCaseInsensitiveContains(searchText) }
        }
    }

    var body: some View {
        HSplitView {
            sidebar.frame(minWidth: 300, idealWidth: 355, maxWidth: 450)
            detail.frame(minWidth: 480, maxWidth: .infinity, maxHeight: .infinity)
        }
        .background(.clear)
        .onChange(of: catalog?.rootPath) { _, _ in
            selectedID = nil; result = nil; localError = nil
            selectFirstIfNeeded()
        }
        .onChange(of: catalog?.endpoints.map(\.id).joined(separator: "|")) { _, _ in validateSelection() }
        .onChange(of: catalog?.requests.map(\.id).joined(separator: "|")) { _, _ in validateSelection() }
        .onChange(of: catalog?.httpCalls.map(\.id).joined(separator: "|")) { _, _ in validateSelection() }
        .onAppear { selectFirstIfNeeded() }
    }
}
