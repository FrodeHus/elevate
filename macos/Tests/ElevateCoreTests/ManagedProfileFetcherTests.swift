import Testing
import Foundation
@testable import ElevateCore

@Suite struct ManagedProfileFetcherTests {
    let url = URL(string: "https://contoso.example/profiles.json")!

    func temporaryCacheURL() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("elevate-tests-\(UUID().uuidString)", isDirectory: true)
            .appendingPathComponent("managed-profiles.json")
    }

    @Test func fetchParsesAndCaches() async throws {
        let cache = temporaryCacheURL()
        let http = StubHTTPClient()
        await http.on("GET", "profiles.json", body: Data(ManagedProfileSetTests.example.utf8))
        let fetcher = ManagedProfileFetcher(http: http, cacheURL: cache)

        let set = try await fetcher.fetch(from: url)
        #expect(set.profiles.map(\.id) == ["prod-incident"])
        #expect(FileManager.default.fileExists(atPath: cache.path))
        #expect(fetcher.cached() == set)

        let request = try #require(await http.requests.first)
        #expect(request.method == "GET")
        #expect(request.headers["Accept"] == "application/json")
    }

    @Test func cachedIsNilWithoutAFileOrWithAnUnreadableOne() throws {
        let cache = temporaryCacheURL()
        let fetcher = ManagedProfileFetcher(http: StubHTTPClient(), cacheURL: cache)
        #expect(fetcher.cached() == nil)

        try FileManager.default.createDirectory(at: cache.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: cache)
        #expect(fetcher.cached() == nil)
    }

    @Test func fetchThrowsOnAnErrorStatusAndLeavesTheCacheAlone() async throws {
        let cache = temporaryCacheURL()
        let http = StubHTTPClient()
        await http.on("GET", "profiles.json", status: 500, body: Data("boom".utf8))
        let fetcher = ManagedProfileFetcher(http: http, cacheURL: cache)

        await #expect(throws: ManagedProfileError.invalid("managed profiles fetch failed with HTTP 500")) {
            try await fetcher.fetch(from: url)
        }
        #expect(!FileManager.default.fileExists(atPath: cache.path))
    }

    @Test func fetchThrowsOnAnOversizedBody() async throws {
        let cache = temporaryCacheURL()
        let http = StubHTTPClient()
        let big = Data(repeating: UInt8(ascii: " "), count: ManagedProfileFetcher.maxBytes + 1)
        await http.on("GET", "profiles.json", body: big)
        let fetcher = ManagedProfileFetcher(http: http, cacheURL: cache)

        await #expect(throws: ManagedProfileError.invalid("managed profiles document is larger than 1 MB")) {
            try await fetcher.fetch(from: url)
        }
        #expect(!FileManager.default.fileExists(atPath: cache.path))
    }

    @Test func fetchRefusesANonHttpsURL() async throws {
        let fetcher = ManagedProfileFetcher(http: StubHTTPClient(), cacheURL: temporaryCacheURL())
        await #expect(throws: ManagedProfileError.invalid("managed profiles URL must be https")) {
            try await fetcher.fetch(from: URL(string: "http://contoso.example/profiles.json")!)
        }
    }

    @Test func fetchThrowsOnAnUnparsableBody() async throws {
        let cache = temporaryCacheURL()
        let http = StubHTTPClient()
        await http.on("GET", "profiles.json", body: Data(#"{"version":2}"#.utf8))
        let fetcher = ManagedProfileFetcher(http: http, cacheURL: cache)

        await #expect(throws: ManagedProfileError.invalid("version 2 is not supported")) {
            try await fetcher.fetch(from: url)
        }
        #expect(!FileManager.default.fileExists(atPath: cache.path))
    }
}
