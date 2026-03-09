import { redirect } from "next/navigation";
import { getSession } from "@/lib/session";
import { LoginClient } from "@/app/login/login-client";

export default async function LoginPage() {
  const session = await getSession();
  if (session.isAuthenticated) {
    redirect("/panel");
  }

  return <LoginClient />;
}