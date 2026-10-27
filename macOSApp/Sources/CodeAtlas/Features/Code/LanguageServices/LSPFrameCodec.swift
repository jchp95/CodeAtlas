import Foundation

/// LSP 3.x uses JSON-RPC messages with an ASCII header and Content-Length byte count.
/// This codec supports split headers/bodies and multiple frames received in one read.
struct LSPFrameCodec {
    private(set) var bufferedData = Data()
    let maximumPayloadBytes: Int

    init(maximumPayloadBytes: Int = 16 * 1024 * 1024) {
        self.maximumPayloadBytes = max(1, maximumPayloadBytes)
    }

    mutating func append(_ data: Data) throws -> [Data] {
        bufferedData.append(data)
        var frames: [Data] = []
        let separator = Data([13, 10, 13, 10])

        while let headerRange = bufferedData.range(of: separator) {
            let headerData = bufferedData.subdata(in: 0..<headerRange.lowerBound)
            guard let header = String(data: headerData, encoding: .ascii) else {
                throw LSPFrameCodecError.invalidHeaderEncoding
            }
            guard let contentLength = Self.contentLength(in: header) else {
                throw LSPFrameCodecError.missingContentLength
            }
            guard contentLength >= 0, contentLength <= maximumPayloadBytes else {
                throw LSPFrameCodecError.payloadTooLarge(contentLength)
            }
            let bodyStart = headerRange.upperBound
            guard bufferedData.count >= bodyStart + contentLength else { break }
            let bodyRange = bodyStart..<(bodyStart + contentLength)
            frames.append(bufferedData.subdata(in: bodyRange))
            bufferedData.removeSubrange(0..<(bodyStart + contentLength))
        }

        if bufferedData.count > maximumPayloadBytes + 4096 {
            throw LSPFrameCodecError.bufferLimitExceeded
        }
        return frames
    }

    static func frame(_ payload: Data) -> Data {
        var result = Data("Content-Length: \(payload.count)\r\n\r\n".utf8)
        result.append(payload)
        return result
    }

    private static func contentLength(in header: String) -> Int? {
        for line in header.components(separatedBy: "\r\n") {
            let parts = line.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
            guard parts.count == 2,
                  parts[0].trimmingCharacters(in: .whitespaces).caseInsensitiveCompare("Content-Length") == .orderedSame else { continue }
            return Int(parts[1].trimmingCharacters(in: .whitespaces))
        }
        return nil
    }
}

enum LSPFrameCodecError: LocalizedError, Equatable {
    case invalidHeaderEncoding
    case missingContentLength
    case payloadTooLarge(Int)
    case bufferLimitExceeded

    var errorDescription: String? {
        switch self {
        case .invalidHeaderEncoding: return "El servidor de lenguaje envió una cabecera que no es ASCII."
        case .missingContentLength: return "El servidor de lenguaje envió una trama sin Content-Length."
        case .payloadTooLarge(let count): return "La trama LSP supera el límite permitido (\(count) bytes)."
        case .bufferLimitExceeded: return "El búfer del protocolo LSP superó el límite de seguridad."
        }
    }
}
