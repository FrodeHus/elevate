import Foundation
import Testing
import ElevateCore
@testable import Elevate

@MainActor
struct ProfileDeactivationTests {
    private func setup(_ http: StubHTTPClient) async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]; state.tenants = [Sample.tenant()]
        let network = NetworkMonitor(forcedOnline: false)
        let model = await makeModel(state: state, http: http, network: network, ownAppViaLoopback: false)
        network.simulatePathChange(online: true)
        model.roles[Sample.tenantKey] = [Sample.role(Sample.entraKey, name: "Reader")]
        return model
    }

    @Test func runRecordsActualOutcomesAndIgnoresSkippedRolesAfterEdits() async {
        let model = await setup(StubHTTPClient())
        defer { cleanup(model) }
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey, Sample.groupKey])!
        let run = ProfileRun(profileId: profile.id, profileName: profile.name)
        model.state.profileRuns.append(run)
        let assignment = ActiveAssignment(roleKey: Sample.entraKey, assignmentId: "request", startDateTime: .now,
                                          endDateTime: .now.addingTimeInterval(3600), status: .active, scheduleId: "owned")
        model.recordProfileOutcome(.init(roleKey: Sample.entraKey, result: .activated(assignment)), runId: run.id)
        model.recordProfileOutcome(.init(roleKey: Sample.entraKey, result: .activated(assignment)), runId: run.id)
        model.updateProfile(id: profile.id, keys: [Sample.azureKey])
        #expect(model.state.profileRuns[0].entries.count == 1)
        #expect(model.state.profileRuns[0].entries[0].assignment == assignment)
        #expect(model.state.profileRuns[0].entries[0].displayName == "Reader")
    }

    @Test func completedRunsStayInHistoryButDoNotOfferAnotherAction() async {
        let model = await setup(StubHTTPClient())
        defer { cleanup(model) }
        let profileId = UUID()
        let completed = ProfileRun.Entry(assignment: Sample.assignment(Sample.entraKey), displayName: "Reader", completed: true)
        model.state.profileRuns = [ProfileRun(profileId: profileId, profileName: "Ops", entries: [completed])]

        #expect(model.profileRuns(for: profileId).isEmpty)
        #expect(model.profileRunHistory(for: profileId).count == 1)
    }

    @Test func stalePlanCannotReactivatePreexistingRole() async {
        let http = StubHTTPClient()
        let model = await setup(http)
        defer { cleanup(model) }
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey])!
        let plan = model.plan(for: profile.id)
        model.active[Sample.entraKey] = Sample.assignment(Sample.entraKey)
        let outcomes = await model.runProfile(id: profile.id, items: plan, justification: "test", ticket: nil)
        #expect(outcomes.isEmpty)
        #expect(model.state.profileRuns.allSatisfy { $0.entries.isEmpty })
        #expect(await http.requests.filter { $0.method == "POST" }.isEmpty)
        #expect(model.active[Sample.entraKey] != nil)
    }

    @Test func profileExecutionPersistsOnlyAssignmentsItCreated() async {
        let http = StubHTTPClient()
        await http.on("GET", "/me?", body: Data(#"{"id":"principal"}"#.utf8))
        await http.on("GET", "roleAssignmentScheduleInstances", body: Data(#"{"value":[]}"#.utf8))
        await http.on("GET", "roleAssignmentScheduleRequests", body: Data(#"{"value":[]}"#.utf8))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201,
            body: Data(#"{"id":"created-request","targetScheduleId":"created-schedule","roleDefinitionId":"role-def","directoryScopeId":"/","status":"Provisioned"}"#.utf8))
        let model = await setup(http)
        defer { cleanup(model) }
        let profile = model.saveProfile(name: "Ops", keys: [Sample.entraKey, Sample.groupKey])!
        model.roles[Sample.tenantKey, default: []].append(Sample.role(Sample.groupKey, name: "Group"))
        model.active[Sample.groupKey] = Sample.assignment(Sample.groupKey)
        let outcomes = await model.runProfile(id: profile.id, items: model.plan(for: profile.id), justification: "test", ticket: nil)
        #expect(outcomes.count == 1)
        #expect(model.state.profileRuns.count == 1)
        #expect(model.state.profileRuns.first?.entries.map { $0.assignment.roleKey } == [Sample.entraKey])
        #expect(model.state.profileRuns.first?.entries.first?.assignment.scheduleId == "created-schedule")
        #expect(await http.requests.filter { $0.method == "POST" }.count == 1)
    }

    @Test func failedRoleRetriesButCompletedRoleNeverWritesAgain() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "/me?", body: Data(#"{"id":"principal"}"#.utf8))
        let start = Date.now.addingTimeInterval(-600), end = Date.now.addingTimeInterval(3600)
        let other = Sample.key(.entraDirectory(roleDefinitionId: "other", directoryScopeId: "/"))
        let instances: [String: Any] = ["value": ["role-def", "other"].map { id in
            ["id": "instance-" + id, "roleAssignmentScheduleId": "schedule-" + id,
             "roleDefinitionId": id, "directoryScopeId": "/", "assignmentType": "Activated",
             "startDateTime": GraphJSON.encoderDateString(start), "endDateTime": GraphJSON.encoderDateString(end)]
        }]
        await http.on("GET", "roleAssignmentScheduleInstances", body: try JSONSerialization.data(withJSONObject: instances))
        await http.on("GET", "roleAssignmentScheduleRequests", body: Data(#"{"value":[]}"#.utf8))
        await http.on("POST", "roleAssignmentScheduleRequests") { request in
            let body = (try? JSONSerialization.jsonObject(with: request.body ?? Data())) as? [String: Any]
            if body?["roleDefinitionId"] as? String == "other" {
                return HTTPResponse(status: 400, body: Data(#"{"error":{"code":"BadRequest","message":"temporary failure"}}"#.utf8))
            }
            return HTTPResponse(status: 201, body: Data(#"{"id":"deactivation","status":"Provisioned","roleDefinitionId":"role-def"}"#.utf8))
        }
        let model = await setup(http)
        defer { cleanup(model) }
        let entries = [Sample.entraKey, other].map { key in
            let id = key == Sample.entraKey ? "role-def" : "other"
            return ProfileRun.Entry(assignment: ActiveAssignment(roleKey: key, assignmentId: "request-" + id,
                startDateTime: start, endDateTime: end, status: .active, scheduleId: "schedule-" + id), displayName: id)
        }
        let run = ProfileRun(profileId: UUID(), profileName: "Ops", entries: entries)
        model.state.profileRuns = [run]
        await model.deactivateProfileRun(run.id)
        #expect(model.state.profileRuns[0].entries.map(\.completed) == [true, false])
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: Data(#"{"id":"deactivation","status":"Provisioned","roleDefinitionId":"role-def"}"#.utf8))
        await model.deactivateProfileRun(run.id)
        #expect(model.state.profileRuns[0].entries.allSatisfy { $0.completed })
        #expect(await http.requests.filter { $0.method == "POST" }.count == 3)
    }
}
