import Foundation

/// Wire shapes for a Graph/ARM schedule's expiration, shared by the providers that read one.
struct ScheduleExpiration: Decodable, Sendable { let type: String?; let duration: String?; let endDateTime: Date? }
struct ScheduleInfo: Decodable, Sendable { let startDateTime: Date?; let expiration: ScheduleExpiration? }
