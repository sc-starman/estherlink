import { type OmniSession } from "@/lib/session";
import {
  type ClientConfigPayload,
  type GatewayClientCreateOptions,
  type GatewayClientRecord,
  type GatewayInboundSnapshot,
  type GatewayProtocolProvider,
  type ProtocolBackupInput,
  type ProtocolBackupPayload
} from "@/lib/providers/types";
import { type ProtocolCapabilityDescriptor } from "@/lib/protocol-capabilities";

const UNSUPPORTED_MESSAGE = "Per-client management is not supported for this protocol.";

export class SharedCredentialSingboxProvider implements GatewayProtocolProvider {
  public readonly protocolId: string;
  private readonly inboundProtocol: string;
  private readonly inboundRemark: string;
  private readonly capabilities: ProtocolCapabilityDescriptor;

  public constructor(protocolId: string, inboundProtocol: string, inboundRemark: string, capabilities: ProtocolCapabilityDescriptor) {
    this.protocolId = protocolId;
    this.inboundProtocol = inboundProtocol;
    this.inboundRemark = inboundRemark;
    this.capabilities = capabilities;
  }

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const portRaw = Number(process.env.SINGBOX_PUBLIC_PORT ?? 443);
    const port = Number.isFinite(portRaw) && portRaw > 0 ? Math.trunc(portRaw) : 443;
    return {
      inbound: {
        id: 1,
        protocol: this.inboundProtocol,
        port,
        remark: this.inboundRemark,
        enable: true
      },
      clients: [],
      capabilities: this.capabilities
    };
  }

  public async addClient(_session: OmniSession, _email: string, _options?: GatewayClientCreateOptions): Promise<GatewayClientRecord> {
    throw new Error(UNSUPPORTED_MESSAGE);
  }

  public async updateClient(_session: OmniSession, _client: GatewayClientRecord): Promise<void> {
    throw new Error(UNSUPPORTED_MESSAGE);
  }

  public async deleteClient(_session: OmniSession, _clientId: string): Promise<void> {
    throw new Error(UNSUPPORTED_MESSAGE);
  }

  public async buildClientConfig(_session: OmniSession, _request: Request, _clientId: string): Promise<ClientConfigPayload> {
    throw new Error(UNSUPPORTED_MESSAGE);
  }

  public async exportBackup(_session: OmniSession): Promise<ProtocolBackupPayload> {
    throw new Error(UNSUPPORTED_MESSAGE);
  }

  public async importBackup(_session: OmniSession, _input: ProtocolBackupInput): Promise<void> {
    throw new Error(UNSUPPORTED_MESSAGE);
  }
}

