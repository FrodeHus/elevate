import SwiftUI

/// A role's name as a row draws it: one line that truncates, with a small crown after it when the role
/// is privileged. The crown never shrinks, so it stays visible when the name is cut.
struct RoleNameLabel: View {
    let name: String
    let isPrivileged: Bool

    init(_ name: String, isPrivileged: Bool) {
        self.name = name
        self.isPrivileged = isPrivileged
    }

    var body: some View {
        HStack(spacing: 4) {
            Text(name).lineLimit(1)
            if isPrivileged {
                Image(systemName: "crown.fill")
                    .font(.caption2)
                    .foregroundStyle(.orange)
                    .fixedSize()
                    .layoutPriority(1)
                    .help("Privileged role")
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(isPrivileged ? "\(name), privileged role" : name)
    }
}
