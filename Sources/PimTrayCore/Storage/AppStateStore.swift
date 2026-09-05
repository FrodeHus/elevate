import Foundation

public actor AppStateStore {
    private let fileURL: URL
    /// Generation of the newest state written; out-of-order saves are dropped.
    private var lastAppliedGeneration: UInt64 = 0
    private var nextGeneration: UInt64 = 0

    public static var defaultDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let current = base.appendingPathComponent("Elevate", isDirectory: true)
        let legacy = base.appendingPathComponent("PimTray", isDirectory: true)
        // One-time move of state saved under the app's former name.
        if !FileManager.default.fileExists(atPath: current.path), FileManager.default.fileExists(atPath: legacy.path) {
            try? FileManager.default.moveItem(at: legacy, to: current)
        }
        return current
    }

    public init(directory: URL = AppStateStore.defaultDirectory) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        fileURL = directory.appendingPathComponent("state.json")
    }

    public func load() throws -> AppState {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return AppState() }
        return try GraphJSON.decoder.decode(AppState.self, from: Data(contentsOf: fileURL))
    }

    /// Moves an unreadable `state.json` aside so the next save cannot silently destroy it.
    /// Returns the backup URL, or nil when there was no file to move.
    public func quarantineCorruptFile() throws -> URL? {
        guard FileManager.default.fileExists(atPath: fileURL.path) else { return nil }
        let backup = fileURL.deletingLastPathComponent().appendingPathComponent("state.json.bak")
        try? FileManager.default.removeItem(at: backup)
        try FileManager.default.moveItem(at: fileURL, to: backup)
        return backup
    }

    public func save(_ state: AppState) throws {
        nextGeneration += 1
        try save(state, generation: nextGeneration)
    }

    /// Writes `state` unless a newer generation has already been written.
    public func save(_ state: AppState, generation: UInt64) throws {
        guard generation >= lastAppliedGeneration else { return }
        let encoder = GraphJSON.encoder
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(state).write(to: fileURL, options: .atomic)
        lastAppliedGeneration = generation
        nextGeneration = max(nextGeneration, generation)
    }
}
