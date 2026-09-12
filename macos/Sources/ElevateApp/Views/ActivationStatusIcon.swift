import SwiftUI
import ElevateCore

/// A request may pulse indefinitely. Only a confirmed result starts the morph.
struct ActivationIconPlayback {
    enum Phase: Equatable { case hidden, working, success }
    static let morphDuration = 1.4
    private(set) var phase: Phase = .hidden
    private var started = 0.0
    private var morphs = false

    mutating func update(_ next: Phase, at time: Double) {
        guard next != phase else { return }
        morphs = phase == .working && next == .success
        phase = next
        started = time
    }

    struct Sample {
        let pulseTime: Double
        let morphTime: Double?
        let animating: Bool
    }

    func sample(at time: Double, reduceMotion: Bool) -> Sample {
        let elapsed = max(0, time - started)
        switch phase {
        case .working:
            return Sample(pulseTime: reduceMotion ? 0 : elapsed, morphTime: nil, animating: !reduceMotion)
        case .success:
            let t = reduceMotion || !morphs ? Self.morphDuration : min(elapsed, Self.morphDuration)
            return Sample(pulseTime: 0, morphTime: t, animating: t < Self.morphDuration)
        case .hidden:
            return Sample(pulseTime: 0, morphTime: nil, animating: false)
        }
    }
}

/// Shared with Windows; these points were traced from the shipped 1024px app icon.
struct ElevationMotion: Decodable {
    struct Frame: Decodable { let upper: [[Double]]; let lower: [[Double]] }
    let interval: Double
    let frames: [Frame]
    static let shared: Self? = {
        guard let url = Bundle.main.url(forResource: "elevation-motion", withExtension: "json"),
              let data = try? Data(contentsOf: url), let motion = try? JSONDecoder().decode(Self.self, from: data),
              motion.interval > 0, motion.frames.count > 1,
              let first = motion.frames.first,
              motion.frames.allSatisfy({ $0.upper.count == first.upper.count && $0.lower.count == first.lower.count
                  && ($0.upper + $0.lower).allSatisfy { $0.count == 2 && $0.allSatisfy(\.isFinite) } }) else { return nil }
        return motion
    }()
}

struct ActivationStatusIcon: View {
    enum Direction { case upward, downward }
    let phase: ActivationIconPlayback.Phase
    var direction: Direction = .upward
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var playback = ActivationIconPlayback()
    @State private var paused = false

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: paused || reduceMotion)) { context in
            let sample = playback.sample(at: context.date.timeIntervalSinceReferenceDate,
                                         reduceMotion: reduceMotion || (paused && phase == .success))
            Canvas { context, size in
                guard let motion = ElevationMotion.shared else {
                    context.draw(Image(systemName: phase == .success ? "checkmark.circle" : (direction == .downward ? "chevron.down.2" : "chevron.up.2")),
                                 at: CGPoint(x: size.width / 2, y: size.height / 2))
                    return
                }
                let position = min((sample.morphTime ?? 0) / motion.interval, Double(motion.frames.count - 1))
                let index = Int(position), fraction = position - Double(index)
                let a = motion.frames[index], b = motion.frames[min(index + 1, motion.frames.count - 1)]
                let green = smooth(((sample.morphTime ?? 0) - 0.6) / 0.8)
                for (upper, from, to) in [(true, a.upper, b.upper), (false, a.lower, b.lower)] {
                    let wave = max(0, sample.pulseTime - (upper ? 0.12 : 0))
                    let pulse = sample.morphTime == nil ? pow(sin(.pi * wave / 0.74), 2) : 0
                    let points = zip(from, to).map { p, q in
                        let y = (p[1] + (q[1] - p[1]) * fraction - 9 * pulse - 32) / 192
                        // Reflect the working chevrons, then restore the upright check during the morph.
                        let reflection = direction == .downward ? 1 - smooth((sample.morphTime ?? 0) / ActivationIconPlayback.morphDuration) : 0
                        return CGPoint(x: (p[0] + (q[0] - p[0]) * fraction - 32) / 192 * size.width,
                                       y: (y + (1 - 2 * y) * reflection) * size.height)
                    }
                    var path = Path()
                    path.addLines(points); path.closeSubpath()
                    // Accent strokes read on both light and dark backgrounds at row size.
                    var layer = context
                    layer.opacity = upper ? 1 : 0.65 + 0.35 * green
                    layer.fill(path, with: .color(Color.accentColor.mix(with: .green, by: green)))
                }
            }
        }
        .frame(width: 20, height: 20)
        .accessibilityHidden(true) // The adjacent status text supplies the accessible label.
        .onDisappear { paused = true }
        .task(id: phase) {
            playback.update(phase, at: Date.now.timeIntervalSinceReferenceDate)
            paused = false
            if phase == .success {
                try? await Task.sleep(for: .seconds(ActivationIconPlayback.morphDuration))
                if !Task.isCancelled { paused = true }
            } else if phase == .hidden { paused = true }
        }
    }

    private func smooth(_ value: Double) -> Double {
        let x = min(1, max(0, value))
        return x * x * (3 - 2 * x)
    }
}

/// Keeping this view in place across result changes preserves the working → success transition.
struct ActivationProgressLabel: View {
    let result: ActivationOutcome.Result?
    let running: Bool

    var phase: ActivationIconPlayback.Phase {
        switch result {
        case .activated: .success
        case nil: running ? .working : .hidden
        default: .hidden
        }
    }

    var body: some View {
        HStack(spacing: 5) {
            if phase != .hidden { ActivationStatusIcon(phase: phase) }
            switch result {
            case .activated: Text("Active").foregroundStyle(.green)
            case .scheduled: Label("Scheduled", systemImage: "calendar").foregroundStyle(.blue)
            case .pendingApproval: Label("Pending", systemImage: "clock").foregroundStyle(.orange)
            case .failed(let error):
                Text(error.userMessage).foregroundStyle(.red).lineLimit(1).help(error.userMessage)
            case nil: if running { Text("Activating…").foregroundStyle(.secondary) }
            }
        }
        .font(.caption)
        .accessibilityElement(children: .combine)
    }
}

/// Deactivation shares the confirmed-result animation, with downward working chevrons.
struct DeactivationProgressLabel: View {
    let phase: DeactivationPhase
    var showsIcon = true

    var iconPhase: ActivationIconPlayback.Phase {
        switch phase {
        case .working: .working
        case .succeeded: .success
        case .failed, .blocked: .hidden
        }
    }

    var body: some View {
        HStack(spacing: 5) {
            if showsIcon && iconPhase != .hidden { ActivationStatusIcon(phase: iconPhase, direction: .downward) }
            switch phase {
            case .working: Text("Deactivating…").foregroundStyle(.secondary)
            case .succeeded: Text("Deactivated").foregroundStyle(.green)
            case .failed(let message):
                Label("Failed", systemImage: "exclamationmark.circle").foregroundStyle(.red).help(message)
            case .blocked(let message):
                Label("Blocked", systemImage: "lock").foregroundStyle(.orange).help(message)
            }
        }
        .font(.caption)
        .accessibilityElement(children: .combine)
    }
}

/// Keeps the role's leading status position while showing confirmed deactivation feedback.
struct RoleStatusIndicator: View {
    let status: ActiveAssignment.Status?
    let deactivation: DeactivationPhase?
    @State private var successVisible = true

    var body: some View {
        Group {
            if let deactivation, deactivation != .succeeded || successVisible {
                switch deactivation {
                case .working, .succeeded:
                    ActivationStatusIcon(phase: deactivation == .working ? .working : .success, direction: .downward)
                        .accessibilityHidden(false)
                        .accessibilityLabel(deactivation == .working ? "Deactivating" : "Deactivated")
                case .failed(let message):
                    Image(systemName: "exclamationmark.circle").foregroundStyle(.red).help(message)
                        .accessibilityLabel("Deactivation failed: \(message)")
                case .blocked(let message):
                    Image(systemName: "lock").foregroundStyle(.orange).help(message)
                        .accessibilityLabel("Deactivation blocked: \(message)")
                }
            } else { StatusDot(status: status) }
        }
        .frame(width: 20, height: 20)
        .task(id: deactivation) {
            successVisible = true
            if deactivation == .succeeded {
                try? await Task.sleep(for: .seconds(3))
                if !Task.isCancelled { successVisible = false }
            }
        }
    }
}
