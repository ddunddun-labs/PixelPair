#!/usr/bin/env node
/**
 * Records the browser half of the README demo and composites it with the
 * deterministic VHS terminal capture. PixelPair must already be running.
 *
 * This maintainer workflow is currently intended for Linux/WSL. The VHS tape
 * and this script intentionally share one fixed /tmp output directory so the
 * generated assets cannot drift apart.
 */
import { spawn } from "node:child_process";
import { mkdir } from "node:fs/promises";
import { chromium } from "@playwright/test";

const baseUrl = (process.env.PIXELPAIR_URL || "http://127.0.0.1:17890").replace(/\/$/, "");
const outputDir = "/tmp/pixelpair-readme-demo";
const browserVideo = `${outputDir}/browser.webm`;
const finalVideo = `${outputDir}/pixelpair-readme-demo.mp4`;
const finalGif = "docs/demo/pixelpair-collaboration.gif";

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: process.cwd(), stdio: "inherit" });
    child.once("error", reject);
    child.once("exit", (code) => code === 0 ? resolve() : reject(new Error(`${command} exited with ${code}`)));
  });
}

async function main() {
  await mkdir(outputDir, { recursive: true });
  await mkdir("docs/demo", { recursive: true });

  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
    await page.goto(baseUrl, { waitUntil: "networkidle" });
    await page.locator("#grid").waitFor();

    // Reset before recording. The visible drawing and MCP change occur after capture starts.
    await page.evaluate(async () => {
      const response = await fetch("/clear", { method: "POST" });
      if (!response.ok) throw new Error(`canvas reset failed: ${response.status}`);
    });

    await page.screencast.start({ path: browserVideo, size: { width: 960, height: 600 } });
    await page.locator("#btnPen").click();
    await page.locator("#btnSymmetry").click();
    const box = await page.locator("#grid").boundingBox();
    if (!box) throw new Error("canvas bounds unavailable");
    // A short manual symmetric mark makes the handoff to the agent visible.
    await page.mouse.click(box.x + 25.5 * (box.width / 64), box.y + 12.5 * (box.height / 64));
    await page.waitForTimeout(2000);

    const terminalCapture = run("vhs", ["scripts/demo/readme-demo.tape"]);
    await page.waitForTimeout(1600);
    await run("node", ["scripts/demo/mcp-demo-request.mjs"]);
    await page.waitForTimeout(3000);
    await run("node", ["scripts/demo/mcp-demo-refine.mjs"]);
    await page.waitForTimeout(4600);
    await run("node", ["scripts/demo/mcp-demo-finalize.mjs"]);
    await terminalCapture;
    await page.waitForTimeout(5000); // Hold the verified final state for viewers.
    await page.screencast.stop();
  } finally {
    await browser.close();
  }

  await run("ffmpeg", [
    "-y", "-i", `${outputDir}/terminal.gif`, "-i", browserVideo,
    "-filter_complex", "[0:v]fps=12,scale=960:140:force_original_aspect_ratio=decrease,pad=960:140:(ow-iw)/2:(oh-ih)/2:color=0x0F131A,tpad=stop_mode=clone:stop_duration=6[terminal];[1:v]fps=12,scale=960:600[browser];[terminal][browser]vstack=inputs=2:shortest=1",
    "-t", "15", "-c:v", "libx264", "-pix_fmt", "yuv420p", finalVideo,
  ]);

  await run("ffmpeg", [
    "-y", "-i", finalVideo,
    "-filter_complex", "[0:v]fps=10,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle",
    "-loop", "0", finalGif,
  ]);

  console.log(`Demo MP4 written to ${finalVideo}`);
  console.log(`README GIF written to ${finalGif}`);
}

main().catch((error) => {
  console.error(error.stack || error.message);
  process.exitCode = 1;
});