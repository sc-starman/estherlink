import { PublicClientConfig } from "@/app/c/[clientId]/public-client-config";

interface PageProps {
  params: Promise<{
    clientId?: string;
  }>;
}

export default async function PublicClientConfigPage({ params }: PageProps) {
  const { clientId } = await params;
  return <PublicClientConfig clientId={String(clientId ?? "").trim()} />;
}
