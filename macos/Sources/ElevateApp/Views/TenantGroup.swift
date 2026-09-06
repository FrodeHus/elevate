import SwiftUI
import ElevateCore

/// A boxed group of sheet rows that belong to one tenant: the tenant name leads, the account is
/// the caption, and a soft fill ties the rows to their header so a mixed-tenant sheet reads at a
/// glance instead of by parsing "user · tenant" lines.
struct TenantGroup<Content: View>: View {
    @Environment(AppModel.self) private var model
    let tenantKey: TenantKey
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Image(systemName: "building.2").font(.caption).foregroundStyle(Color.accentColor)
                Text(model.tenant(tenantKey)?.displayName ?? tenantKey.tenantId).font(.subheadline.weight(.semibold))
                Text("·").foregroundStyle(.tertiary)
                Text(model.identity(tenantKey.identityId)?.upn ?? tenantKey.identityId)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                Spacer(minLength: 0)
            }
            .accessibilityElement(children: .combine)
            content()
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}
