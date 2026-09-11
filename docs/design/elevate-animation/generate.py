"""Generate the standalone Elevate motion assets. Requires numpy and Pillow."""
from pathlib import Path
import math
import json
import numpy as np
from PIL import Image, ImageDraw

OUT = Path(__file__).parent
N = 81
U = np.linspace(0, 1, N)
DURATION = 6.4
FPS = 25

def ease(x):
    x = np.clip(x, 0, 1)
    return x*x*(3-2*x)

def bezier(points, t):
    a,b,c,d = map(np.array, points)
    t = t[:, None]
    return (1-t)**3*a + 3*(1-t)**2*t*b + 3*(1-t)*t*t*c + t**3*d

SOURCE = OUT.parents[2]/'macos/Sources/ElevateApp/Assets.xcassets/AppIcon.appiconset/icon_512@2x.png'
source = Image.open(SOURCE).convert('RGBA')
pixels = np.asarray(source).astype(float)

def trace_chevron(left, right, top_y, bottom_y, extent, power):
    """Follow the strongest source-image edge in each sampled column.

    The search bands distinguish the two chevrons from the soft shadows.
    Cosine-spaced columns retain the rounded tips without fitting a new logo.
    All geometry comes from the 1024px shipped application asset.
    """
    xs = left+(right-left)*(1-np.cos(np.pi*U))/2
    outer,inner = [],[]
    for x in xs:
        col = pixels[:,round(x*4),0]
        derivative = np.diff(col)
        distance = abs((x-127.8)/extent)
        edges=[]
        for sign, guess in [(1,top_y+(148.6-top_y if top_y<100 else 183.3-top_y)*distance**power),
                            (-1,bottom_y+(148.6-bottom_y if top_y<100 else 183.3-bottom_y)*distance**1.1)]:
            lo,hi = int((guess-8)*4),int((guess+8)*4)
            if sign == -1:
                lo,hi = (110*4,162*4) if top_y<100 else (166*4,194*4)
            peak = lo+np.argmax(sign*derivative[lo:hi])
            idx=np.arange(peak-1,peak+2)
            weights=np.maximum(sign*derivative[idx],0)
            edge=np.sum((idx+.5)*weights)/max(weights.sum(),1)
            edges.append(edge/4)
        outer.append((x,edges[0]));inner.append((x,edges[1]))
    outer,inner=np.array(outer),np.array(inner)
    cap_end=np.linspace(outer[-1],inner[-1],13)[1:-1]
    cap_start=np.linspace(inner[0],outer[0],13)[1:-1]
    return np.concatenate((outer,cap_end,inner[::-1],cap_start))

TOP_OUTLINE = trace_chevron(43.4,212.1,62.4,117.5,84.6,1.1)
LOW_OUTLINE = trace_chevron(52.4,202.7,132.0,170.8,75.4,1.12)
CHECK = np.concatenate([
    bezier([(96,130),(103,137),(114,147),(119,147)], U[:41]*2),
    bezier([(119,147),(124,147),(152,118),(161,108)], U[41:]*2-1)
])

def ribbon(points, widths, closure=0):
    tangent = np.gradient(points,axis=0)
    tangent /= np.linalg.norm(tangent,axis=1)[:,None]
    for i in (0,-1):
        tangent[i] = tangent[i]*(1-closure)+np.array([-1.,0.])*closure
        tangent[i] /= np.linalg.norm(tangent[i])
    normal = np.column_stack((tangent[:,1],-tangent[:,0]))
    outer = points + normal*widths[:,None]
    inner = points - normal*widths[:,None]
    a = np.linspace(0,np.pi,13)[1:-1]
    cap_end = points[-1] + widths[-1]*(np.cos(a)[:,None]*normal[-1] + np.sin(a)[:,None]*tangent[-1]*(1-closure))
    cap_start = points[0] + widths[0]*(-np.cos(a)[:,None]*normal[0] - np.sin(a)[:,None]*tangent[0]*(1-closure))
    return np.concatenate((outer,cap_end,inner[::-1],cap_start))

def state(t):
    # Lower chevron leads each pulse; brightness rises with upward travel.
    def pulse(delay):
        q = (t-.25-delay)/.74
        if q < 0 or q >= 3: return 0.
        return math.sin(math.pi*(q%1))**2
    p0,p1 = pulse(.12),pulse(0)
    upper = TOP_OUTLINE.copy(); lower = LOW_OUTLINE.copy()
    upper[:,1] -= 9*p0; lower[:,1] -= 9*p1
    bend = float(ease((t-2.65)/.55))
    wrap = float(ease((t-3.05)/1.0))
    # The first bend opens the roof into an arch. Its tips then trace
    # opposite semicircles, closing at six o'clock without scaling.
    theta = np.pi - wrap*np.pi/2 + U*(np.pi+wrap*np.pi)
    arc = np.column_stack((128+72*np.cos(theta),128+72*np.sin(theta)))
    upper = upper*(1-bend)+ribbon(arc,np.full(N,6.5),float(ease((wrap-.96)/.04)))*bend
    check = float(ease((t-3.12)/.82))
    lower = lower*(1-check)+ribbon(CHECK,np.full(N,6.5))*check
    green = float(ease((t-3.25)/.8))
    ca = (np.array([244,250,255])*(1-green)+np.array([77,239,154])*green).astype(int)
    cb = (np.array([157,201,255])*(1-green)+np.array([77,239,154])*green).astype(int)
    opacity = 1.
    return upper,lower,ca,cb,opacity

def path(poly):
    return 'M'+' L'.join(f'{x:.2f},{y:.2f}' for x,y in poly)+' Z'

def color(c): return '#'+''.join(f'{v:02x}' for v in c)

def shading(t, idx):
    green=float(ease((t-3.25)/.8))
    colors = ([[250,253,255],[205,226,252]],[[149,198,250],[119,147,252]])[idx]
    return [np.rint(np.array(c)*(1-green)+np.array([77,239,154])*green).astype(int) for c in colors]

DEFS = '''<defs>
  <linearGradient id="base" x1="0" y1="0" x2=".6" y2="1"><stop stop-color="#28c8f4"/><stop offset=".5" stop-color="#2278ff"/><stop offset="1" stop-color="#3527ed"/></linearGradient>
  <radialGradient id="shine" cx=".26" cy=".02" r=".9"><stop stop-color="#b3ffff" stop-opacity=".22"/><stop offset="1" stop-color="#91dbff" stop-opacity="0"/></radialGradient>
</defs>
<rect x="18" y="18" width="220" height="220" rx="52" fill="url(#base)"/>
<rect x="18" y="18" width="220" height="220" rx="52" fill="url(#shine)" stroke="#a1dfff" stroke-opacity=".28" stroke-width=".8"/>'''

times = sorted(set([round(i*.05,3) for i in range(85)]+[DURATION]))
states = [state(t) for t in times]
keys = ';'.join(f'{t/DURATION:.6f}' for t in times)
def animation(attr, values):
    return f'<animate attributeName="{attr}" dur="{DURATION}s" fill="freeze" calcMode="linear" keyTimes="{keys}" values="'+ ';'.join(values)+'"/>'

gradients='<defs>'
for idx in (0,1):
    gradients+=f'<linearGradient id="chevron{idx}" x1="0" y1="0" x2="0" y2="1">'
    for end in (0,1):
        gradients+=f'<stop offset="{end}" stop-color="{color(shading(0,idx)[end])}">'
        gradients+=animation('stop-color',[color(shading(t,idx)[end]) for t in times])+'</stop>'
    gradients+='</linearGradient>'
gradients+='</defs>'

svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-labelledby="title desc">\n<title id="title">Elevate — elevation successful</title>\n<desc id="desc">Two chevrons pulse upwards. The upper chevron wraps into a circle as the lower bends into a check. Both become green.</desc>\n'+DEFS+gradients
for idx in (0,1):
    svg += f'<path d="{path(states[0][idx])}" fill="url(#chevron{idx})" fill-rule="nonzero">'
    svg += animation('d',[path(s[idx]) for s in states])
    if idx: svg += animation('opacity',[f'{s[4]:.3f}' for s in states])
    svg += '</path>\n'
svg += '</svg>'
(OUT/'elevate-success.svg').write_text(svg)

# Raster preview uses the same shape samples as the SVG, at 3x resolution.
S = 3
y,x = np.mgrid[0:256*S,0:256*S]/S
f = np.clip(((x-18)*.6+(y-18))/(220*1.36),0,1)
stops = np.array([[40,200,244],[34,120,255],[53,39,237]])
bg = np.zeros((*f.shape,3))
for k in range(3): bg[:,:,k] = np.interp(f,[0,.5,1],stops[:,k])
shine = np.clip(1-np.sqrt(((x-75)/198)**2+((y-22)/198)**2),0,1)*.22
bg = bg*(1-shine[:,:,None])+np.array([179,255,255])*shine[:,:,None]
mask = Image.new('L',(256*S,256*S)); ImageDraw.Draw(mask).rounded_rectangle((18*S,18*S,238*S,238*S),52*S,fill=255)
background = Image.fromarray(bg.astype('uint8')).convert('RGBA'); background.putalpha(mask)
def render(t):
    frame = background.copy()
    a,b,ca,cb,op = state(t)
    for idx,poly,opacity in [(1,b,op),(0,a,1)]:
        c0,c1=shading(t,idx)
        blend=np.clip((np.arange(256*S)/S-poly[:,1].min())/np.ptp(poly[:,1]),0,1)
        rows=c0[None,:]*(1-blend[:,None])+c1[None,:]*blend[:,None]
        overlay=Image.fromarray(np.broadcast_to(rows[:,None,:],(256*S,256*S,3)).astype('uint8')).convert('RGBA')
        shape=Image.new('L',frame.size)
        ImageDraw.Draw(shape).polygon([tuple(p*S) for p in poly],fill=round(opacity*255))
        overlay.putalpha(shape)
        frame = Image.alpha_composite(frame,overlay)
    return frame.resize((384,384),Image.Resampling.LANCZOS)

frames = [render(i/FPS) for i in range(round(DURATION*FPS))]
# An opaque neutral matte prevents GIF transparency fringes; SVG stays transparent.
matte = Image.new('RGB',(384,384),(15,20,31))
rgb = []
for frame in frames:
    im = matte.copy(); im.paste(frame,mask=frame.getchannel('A')); rgb.append(im)
palette_sheet = Image.new('RGB',(384*8,384))
for i,t in enumerate([0,.6,1.4,2.7,3.1,3.4,3.8,4.2]):
    im=matte.copy(); fr=render(t); im.paste(fr,mask=fr.getchannel('A')); palette_sheet.paste(im,(384*i,0))
palette = palette_sheet.quantize(colors=256)
gif = [im.quantize(palette=palette,dither=Image.Dither.NONE) for im in rgb]
gif[0].save(OUT/'elevate-success.gif',save_all=True,append_images=gif[1:],duration=40,loop=0,optimize=False,disposal=2)
render(4.2).save(OUT/'elevate-success.png')
render(0).save(OUT/'elevate-start.png')
# Source overlay is a direct visual check of the extracted outlines.
traced=source.copy()
draw=ImageDraw.Draw(traced)
for outline in (TOP_OUTLINE,LOW_OUTLINE):
    pts=[tuple(p*4) for p in outline]
    draw.line(pts+[pts[0]],fill=(255,60,110,255),width=2)
comparison=Image.new('RGBA',(768,384))
comparison.paste(source.resize((384,384),Image.Resampling.LANCZOS),(0,0))
comparison.paste(render(0),(384,0))
comparison.save(OUT/'source-comparison.png')
traced.save(OUT/'trace-verification.png')
palette_sheet.resize((1536,192),Image.Resampling.LANCZOS).save(OUT/'motion-contact-sheet.png')

preview = '''<!doctype html>
<html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Elevate · Success animation</title>
<style>
*{box-sizing:border-box}body{margin:0;min-height:100svh;background:#0f141f;color:#e8edf5;font:15px system-ui,sans-serif;display:grid;place-items:center}
main{text-align:center;padding:36px 24px}h1{font-size:20px;letter-spacing:-.5px;margin:20px 0 7px}p{color:#9ba8bb;margin:0 0 28px}
.stage{width:min(72vw,360px);height:min(72vw,360px);margin:auto}svg{width:100%;height:100%}
button{font:inherit;color:#e8edf5;background:#222d40;border:1px solid #3a475e;padding:10px 20px;border-radius:24px;cursor:pointer}button:hover{background:#303e55}button:focus-visible{outline:2px solid #4def9a;outline-offset:4px}
label{display:flex;gap:10px;align-items:center;justify-content:center;margin:20px 0;color:#9ba8bb}input{width:210px;accent-color:#4def9a}a{color:#9cbdff;margin:0 8px}
</style>
<main><div class="stage">SVG_HERE</div><h1>Elevate</h1><p>Upward motion. Successful elevation.</p>
<button type="button" id="replay">Replay animation</button>
<label>Timeline<input aria-label="Animation timeline" type="range" id="timeline" min="0" max="6.4" step="0.01" value="0"></label>
<a href="elevate-success.svg" download>Animated SVG</a><a href="elevate-success.gif" download>GIF</a>
</main><script>
const svg=document.querySelector('svg'), timeline=document.querySelector('#timeline');
document.querySelector('#replay').onclick=()=>{svg.setCurrentTime(0);svg.unpauseAnimations()};
timeline.oninput=()=>{svg.pauseAnimations();svg.setCurrentTime(Number(timeline.value))};
if(matchMedia('(prefers-reduced-motion: reduce)').matches){svg.setCurrentTime(4.2);svg.pauseAnimations()}
function tick(){if(!svg.animationsPaused())timeline.value=Math.min(6.4,svg.getCurrentTime());requestAnimationFrame(tick)}tick();
</script></html>'''.replace('SVG_HERE',svg)
(OUT/'preview.html').write_text(preview)
print(f'Generated SVG, GIF ({len(frames)} frames), PNG and preview in {OUT}')

# Both native apps consume the same traced outlines and success poses.
# Pulsing is state-driven by the apps; it is never baked into a timed success loop.
poses=[]
for i in range(29):
    a,b,*_=state(2.65+i*.05)
    poses.append({'upper':np.round(a,3).tolist(),'lower':np.round(b,3).tolist()})
(OUT.parents[2]/'shared/elevation-motion.json').write_text(json.dumps({'interval':.05,'frames':poses},separators=(',',':'))+'\n')
