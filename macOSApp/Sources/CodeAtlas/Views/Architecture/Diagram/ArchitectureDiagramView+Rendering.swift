import SwiftUI
import AppKit

extension ArchitectureDiagramView {
func legend(color: Color, label: String) -> some View {
        HStack(spacing: 4) {
            Circle().fill(color).frame(width: 7, height: 7)
            Text(label)
        }
    }

    func drawLaneBackgrounds(context: inout GraphicsContext, layout: ArchitectureDiagramLayout) {
        for index in 0..<ArchitectureDiagramLayout.laneTitles.count {
            let x = ArchitectureDiagramLayout.horizontalInset + CGFloat(index) * ArchitectureDiagramLayout.columnStride
            let laneRect = CGRect(x: x, y: 12, width: ArchitectureDiagramLayout.nodeWidth, height: layout.height - 24)
            let background = Path(roundedRect: laneRect, cornerRadius: 13)
            context.fill(background, with: .color(Color.primary.opacity(index.isMultiple(of: 2) ? 0.035 : 0.018)))
            context.stroke(background, with: .color(Color.secondary.opacity(0.10)), lineWidth: 1)
            let heading = Text(ArchitectureDiagramLayout.laneTitles[index])
                .font(.system(size: 10, weight: .bold, design: .rounded))
                .tracking(0.7)
                .foregroundColor(.secondary)
            context.draw(heading, at: CGPoint(x: x + ArchitectureDiagramLayout.nodeWidth / 2, y: 34))
        }
    }

    func drawEdges(context: inout GraphicsContext, layout: ArchitectureDiagramLayout) {
        let nodesByID = Dictionary(layout.nodes.map { ($0.project.id, $0) }, uniquingKeysWith: { first, _ in first })
        for edge in layout.edges {
            guard let source = nodesByID[edge.sourceBlockID],
                  let target = nodesByID[edge.targetBlockID] else { continue }
            let geometry = edgeGeometry(source: source, target: target)
            var path = Path()
            path.move(to: geometry.start)
            path.addCurve(to: geometry.end, control1: geometry.control1, control2: geometry.control2)
            let color = edgeColor(edge)
            let dash: [CGFloat] = edge.state == .removed || edge.relation.confidence != "confirmed" ? [5, 4] : []
            context.stroke(path, with: .color(color.opacity(0.9)), style: StrokeStyle(lineWidth: edge.state == .added ? 2.7 : 1.8, lineCap: .round, dash: dash))
            context.fill(arrowHead(tip: geometry.end, angle: geometry.angle), with: .color(color))

            let label = Text(edgeShortLabel(edge.relation.type))
                .font(.system(size: 9, weight: .bold, design: .rounded))
                .foregroundColor(color)
            context.draw(label, at: geometry.labelPoint)
        }
    }

    func edgeColor(_ edge: DiagramEdgeLayout) -> Color {
        switch edge.state {
        case .added: return .green
        case .removed: return .red
        case .normal:
            switch edge.relation.type {
            case "http-call": return .blue
            case "message-flow": return .purple
            case "module-import": return .cyan
            case "project-reference", "workspace-dependency": return .secondary
            case "infrastructure": return .orange
            default: return .gray
            }
        }
    }

    func edgeShortLabel(_ type: String) -> String {
        switch type {
        case "http-call": return "HTTP"
        case "message-flow": return "EVENTO"
        case "module-import": return "IMPORT"
        case "project-reference": return "REF"
        case "workspace-dependency": return "WORKSPACE"
        case "infrastructure": return "INFRA"
        default: return String(type.prefix(12)).uppercased()
        }
    }

    func edgeGeometry(source: DiagramNodeLayout, target: DiagramNodeLayout) -> EdgeGeometry {
        let a = source.frame
        let b = target.frame
        if source.lane == target.lane {
            if a.midY <= b.midY {
                let start = CGPoint(x: a.midX, y: a.maxY)
                let end = CGPoint(x: b.midX, y: b.minY)
                return EdgeGeometry(start: start, end: end,
                    control1: CGPoint(x: start.x + 44, y: start.y + 24),
                    control2: CGPoint(x: end.x + 44, y: end.y - 24),
                    angle: .pi / 2,
                    labelPoint: CGPoint(x: max(start.x, end.x) + 48, y: (start.y + end.y) / 2))
            } else {
                let start = CGPoint(x: a.midX, y: a.minY)
                let end = CGPoint(x: b.midX, y: b.maxY)
                return EdgeGeometry(start: start, end: end,
                    control1: CGPoint(x: start.x - 44, y: start.y - 24),
                    control2: CGPoint(x: end.x - 44, y: end.y + 24),
                    angle: -.pi / 2,
                    labelPoint: CGPoint(x: min(start.x, end.x) - 48, y: (start.y + end.y) / 2))
            }
        }

        if source.lane < target.lane {
            let start = CGPoint(x: a.maxX, y: a.midY)
            let end = CGPoint(x: b.minX, y: b.midY)
            let distance = max(36, (end.x - start.x) * 0.42)
            return EdgeGeometry(start: start, end: end,
                control1: CGPoint(x: start.x + distance, y: start.y),
                control2: CGPoint(x: end.x - distance, y: end.y),
                angle: 0,
                labelPoint: CGPoint(x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 - 9))
        } else {
            let start = CGPoint(x: a.minX, y: a.midY)
            let end = CGPoint(x: b.maxX, y: b.midY)
            let distance = max(36, (start.x - end.x) * 0.42)
            return EdgeGeometry(start: start, end: end,
                control1: CGPoint(x: start.x - distance, y: start.y),
                control2: CGPoint(x: end.x + distance, y: end.y),
                angle: .pi,
                labelPoint: CGPoint(x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 - 9))
        }
    }

    func arrowHead(tip: CGPoint, angle: CGFloat) -> Path {
        let length: CGFloat = 9
        let halfWidth: CGFloat = 4.5
        let base = CGPoint(x: tip.x - cos(angle) * length, y: tip.y - sin(angle) * length)
        let perpendicular = CGPoint(x: -sin(angle) * halfWidth, y: cos(angle) * halfWidth)
        var path = Path()
        path.move(to: tip)
        path.addLine(to: CGPoint(x: base.x + perpendicular.x, y: base.y + perpendicular.y))
        path.addLine(to: CGPoint(x: base.x - perpendicular.x, y: base.y - perpendicular.y))
        path.closeSubpath()
        return path
    }
}
