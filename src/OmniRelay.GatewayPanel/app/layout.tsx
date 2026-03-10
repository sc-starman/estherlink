import type { Metadata } from "next";
import { JetBrains_Mono, Space_Grotesk } from "next/font/google";
import "@/app/globals.css";

const displayFont = Space_Grotesk({
  subsets: ["latin"],
  variable: "--font-display"
});

const monoFont = JetBrains_Mono({
  subsets: ["latin"],
  variable: "--font-mono"
});

export const metadata: Metadata = {
  title: "OmniPanel",
  description: "OmniRelay Gateway Panel"
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  const year = new Date().getFullYear();

  return (
    <html lang="en">
      <body className={`${displayFont.variable} ${monoFont.variable} min-h-screen font-[var(--font-display)]`}>
        <div className="flex min-h-screen flex-col">
          <div className="flex-1">{children}</div>
          <footer className="border-t border-slate-200/80 bg-white/70 px-6 py-4 text-center text-sm text-slate-600 backdrop-blur">
            <span>© {year} OmniRelay. All rights reserved. </span>
            <a
              href="https://omnirelay.net"
              target="_blank"
              rel="noreferrer"
              className="font-medium text-cyan-700 underline decoration-cyan-300 underline-offset-4"
            >
              OmniRelay.net
            </a>
          </footer>
        </div>
      </body>
    </html>
  );
}
