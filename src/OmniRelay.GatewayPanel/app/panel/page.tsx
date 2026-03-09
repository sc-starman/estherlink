import { redirect } from "next/navigation";
import { getSession } from "@/lib/session";
import { PanelClient } from "@/app/panel/panel-client";

export default async function PanelPage() {
  const session = await getSession();
  if (!session.isAuthenticated) {
    redirect("/login");
  }

  return <PanelClient />;
}