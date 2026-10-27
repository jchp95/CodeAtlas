import SwiftUI
import AppKit

/// Shared adaptive visual language for the desktop workspace.
/// Materials and border colors intentionally adapt to light/dark/system appearance.
struct AtlasBackdrop: View {
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .topLeading) {
                Color(nsColor: .windowBackgroundColor)

                Circle()
                    .fill(
                        LinearGradient(
                            colors: [Color.accentColor.opacity(colorScheme == .dark ? 0.27 : 0.12), Color.blue.opacity(colorScheme == .dark ? 0.12 : 0.045)],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                    )
                    .frame(width: 760, height: 760)
                    .blur(radius: 115)
                    .offset(x: -280, y: -430)

                Circle()
                    .fill(
                        LinearGradient(
                            colors: [Color.purple.opacity(colorScheme == .dark ? 0.17 : 0.07), Color.cyan.opacity(colorScheme == .dark ? 0.09 : 0.035)],
                            startPoint: .topLeading,
                            endPoint: .bottomTrailing
                        )
                    )
                    .frame(width: 680, height: 680)
                    .blur(radius: 120)
                    .offset(x: max(proxy.size.width - 520, 200), y: 100)

                LinearGradient(
                    colors: [Color.clear, Color(nsColor: .windowBackgroundColor).opacity(colorScheme == .dark ? 0.22 : 0.36)],
                    startPoint: .top,
                    endPoint: .bottom
                )

                Rectangle()
                    .fill(.ultraThinMaterial)
                    .opacity(colorScheme == .dark ? 0.22 : 0.36)
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }
}

struct AtlasGlassSurface: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme
    var cornerRadius: CGFloat = 16
    var highlighted: Bool = false
    var shadow: Bool = true

    func body(content: Content) -> some View {
        content
            .background {
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .fill(.regularMaterial)
                    .overlay {
                        RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                            .fill((colorScheme == .dark ? Color.white : Color.black).opacity(colorScheme == .dark ? 0.035 : 0.018))
                    }
            }
            .overlay {
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(
                        highlighted
                            ? Color.accentColor.opacity(colorScheme == .dark ? 0.8 : 0.65)
                            : (colorScheme == .dark ? Color.white.opacity(0.105) : Color.black.opacity(0.085)),
                        lineWidth: highlighted ? 1.4 : 0.8
                    )
            }
            .shadow(
                color: Color.black.opacity(shadow ? (colorScheme == .dark ? 0.19 : 0.055) : 0),
                radius: shadow ? 18 : 0,
                x: 0,
                y: shadow ? 7 : 0
            )
    }
}

extension View {
    func atlasGlassCard(cornerRadius: CGFloat = 16, highlighted: Bool = false, shadow: Bool = true) -> some View {
        modifier(AtlasGlassSurface(cornerRadius: cornerRadius, highlighted: highlighted, shadow: shadow))
    }
}

struct AtlasPageHeading: View {
    let eyebrow: String
    let title: String
    let detail: String
    var systemImage: String? = nil

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.title2.weight(.semibold))
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 48, height: 48)
                    .background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            }
            VStack(alignment: .leading, spacing: 5) {
                Text(eyebrow.uppercased())
                    .font(.system(size: 10, weight: .bold, design: .rounded))
                    .tracking(1.2)
                    .foregroundStyle(Color.accentColor)
                Text(title)
                    .font(.system(.largeTitle, design: .rounded).weight(.bold))
                    .foregroundStyle(.primary)
                Text(detail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(.bottom, 3)
    }
}

struct AtlasStatusPill: View {
    let title: String
    let color: Color

    var body: some View {
        Text(title)
            .font(.system(size: 10, weight: .semibold, design: .rounded))
            .foregroundStyle(color)
            .padding(.horizontal, 8)
            .padding(.vertical, 4)
            .background(color.opacity(0.12), in: Capsule())
            .overlay(Capsule().stroke(color.opacity(0.18), lineWidth: 0.7))
    }
}
