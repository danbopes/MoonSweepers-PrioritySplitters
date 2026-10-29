"""Wait for ComfyUI, queue a Flux Kontext text-to-image render of a lunar backdrop, wait for it, copy it out."""
import json, time, urllib.request, urllib.error, shutil, sys, os, random
BASE = "http://127.0.0.1:8188"
OUT = sys.argv[1]
PROMPT = ("Wide cinematic shot of the Moon's surface at night, fine pale grey regolith with small craters and scattered "
          "rocks stretching to the horizon, the ground gently lit by cool blue-white light from the upper left, a tiny "
          "distant lunar base with a few warm lights far on the horizon, pitch black sky with faint stars, the Earth small "
          "and dim low in the sky, clean and uncluttered foreground, muted desaturated palette, photoreal, no text, no people")
seed = random.randint(1, 2**31)
wf = {
  "1": {"class_type": "UNETLoader", "inputs": {"unet_name": "flux1-dev-kontext_fp8_scaled.safetensors", "weight_dtype": "default"}},
  "2": {"class_type": "DualCLIPLoader", "inputs": {"clip_name1": "clip_l.safetensors", "clip_name2": "t5xxl_fp8_e4m3fn_scaled.safetensors", "type": "flux"}},
  "3": {"class_type": "VAELoader", "inputs": {"vae_name": "ae.safetensors"}},
  "4": {"class_type": "CLIPTextEncode", "inputs": {"text": PROMPT, "clip": ["2", 0]}},
  "5": {"class_type": "FluxGuidance", "inputs": {"guidance": 2.5, "conditioning": ["4", 0]}},
  "6": {"class_type": "ConditioningZeroOut", "inputs": {"conditioning": ["4", 0]}},
  "7": {"class_type": "EmptySD3LatentImage", "inputs": {"width": 1344, "height": 768, "batch_size": 1}},
  "8": {"class_type": "KSampler", "inputs": {"model": ["1", 0], "positive": ["5", 0], "negative": ["6", 0], "latent_image": ["7", 0],
         "seed": seed, "steps": 20, "cfg": 1.0, "sampler_name": "euler", "scheduler": "simple", "denoise": 1.0}},
  "9": {"class_type": "VAEDecode", "inputs": {"samples": ["8", 0], "vae": ["3", 0]}},
  "10": {"class_type": "SaveImage", "inputs": {"images": ["9", 0], "filename_prefix": "prioritysplitters_backdrop"}},
}
for _ in range(180):
    try:
        urllib.request.urlopen(BASE + "/system_stats", timeout=3).read(); break
    except Exception:
        time.sleep(2)
else:
    sys.exit("ComfyUI did not come up")
t0 = time.time()
req = urllib.request.Request(BASE + "/prompt", data=json.dumps({"prompt": wf}).encode(), headers={"Content-Type": "application/json"})
try:
    pid = json.load(urllib.request.urlopen(req))["prompt_id"]
except urllib.error.HTTPError as e:
    sys.exit("queue refused: " + e.read().decode()[:2000])
print("queued", pid, "seed", seed, flush=True)
while True:
    time.sleep(5)
    hist = json.load(urllib.request.urlopen(BASE + f"/history/{pid}"))
    if pid in hist:
        h = hist[pid]
        if h.get("status", {}).get("status_str") == "error":
            sys.exit("render failed: " + json.dumps(h["status"])[:3000])
        imgs = h["outputs"]["10"]["images"]
        src = os.path.join(os.path.expanduser("~/comfy/ComfyUI/output"), imgs[0]["subfolder"], imgs[0]["filename"])
        shutil.copy(src, OUT)
        print(f"done in {time.time()-t0:.0f}s -> {OUT}", flush=True)
        break
