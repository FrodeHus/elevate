import Testing
import Foundation
@testable import ElevateCore

@Suite struct ErrorLogTests {
    @Test func appendsAndReturnsInOldestToNewestOrder() {
        var log = ErrorLog()
        let d1 = Date(timeIntervalSince1970: 1)
        let d2 = Date(timeIntervalSince1970: 2)
        log.append("first", at: d1)
        log.append("second", at: d2)
        #expect(log.entries.map(\.message) == ["first", "second"])
        #expect(log.entries.map(\.date) == [d1, d2])
    }

    @Test func capsAtCapacityKeepingNewest() {
        var log = ErrorLog(capacity: 3)
        for i in 0..<5 {
            log.append("msg\(i)", at: Date(timeIntervalSince1970: Double(i)))
        }
        #expect(log.entries.map(\.message) == ["msg2", "msg3", "msg4"])
    }

    @Test func defaultCapacityIs50() {
        var log = ErrorLog()
        for i in 0..<60 {
            log.append("msg\(i)", at: Date(timeIntervalSince1970: Double(i)))
        }
        #expect(log.entries.count == 50)
        #expect(log.entries.first?.message == "msg10")
        #expect(log.entries.last?.message == "msg59")
    }

    @Test func equatable() {
        var a = ErrorLog(capacity: 5)
        var b = ErrorLog(capacity: 5)
        let d = Date(timeIntervalSince1970: 10)
        a.append("x", at: d)
        b.append("x", at: d)
        #expect(a == b)
    }
}
