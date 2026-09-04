import Foundation

public actor AppStateStore {
    private let fileURL: URL

    public static var defaultDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("PimTray", isDirectory: true)
    }

    public init(directory: URL = AppStateStore.defaultDirectory) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("state.json")
    }

    public func load() throws -> AppState {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return AppState() }
        return try GraphJSON.decoder.decode(AppState.self, from: Data(contentsOf: fileURL))
    }

    public func save(_ state: AppState) throws {
        let encoder = GraphJSON.encoder
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(state).write(to: fileURL, options: .atomic)
    }
}
