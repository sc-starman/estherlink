import { redirect } from "next/navigation";
import { getSession } from "@/lib/session";
import { ChangePasswordClient } from "@/app/change-password/change-password-client";

export default async function ChangePasswordPage() {
  const session = await getSession();
  if (!session.isAuthenticated) {
    redirect("/login");
  }

  return <ChangePasswordClient username={session.username ?? ""} />;
}