"""Compose offline WPF fixture renders. Requires Pillow; no screen/account access."""
from pathlib import Path
import sys, math
from PIL import Image, ImageDraw, ImageFont, ImageFilter

source=Path(sys.argv[1]); out=Path(sys.argv[2]); out.mkdir(parents=True,exist_ok=True)
font_dir=Path(sys.argv[3]) if len(sys.argv)>3 else Path('C:/Windows/Fonts')
def font(size,bold=False):
    return ImageFont.truetype(str(font_dir/('segoeuib.ttf' if bold else 'segoeui.ttf')),size)
def backdrop(w,h):
    im=Image.new('RGB',(w,h));p=im.load()
    for y in range(h):
        for x in range(w):
            g=math.exp(-(((x-w*.23)/(w*.65))**2+((y-h*.42)/(h*.9))**2)*3)
            b=math.exp(-(((x-w*.9)/(w*.48))**2+((y-h*.9)/(h*.7))**2)*3)
            p[x,y]=(int(15+5*g+9*b),int(21+28*g+7*b),int(25+24*g+27*b))
    return im
def text(im,xy,t,size=22,color='#c2d2d1',bold=False):
    ImageDraw.Draw(im).text(xy,t,font=font(size,bold),fill=color)
def stamp(im):
    d=ImageDraw.Draw(im);d.rounded_rectangle((im.width-266,30,im.width-30,64),radius=17,fill='#182f2d',outline='#32524c')
    text(im,(im.width-249,36),'SYNTHETIC DEMO DATA',15,'#a9d7ca',True)
def paste(im,shot,width,xy,crop=True):
    shot=shot.convert('RGBA')
    if crop:shot=shot.crop(shot.getbbox())
    shot=shot.resize((width,round(shot.height*width/shot.width)),Image.Resampling.LANCZOS)
    shadow=Image.new('RGBA',im.size);mask=shot.getchannel('A');shadow.paste((0,0,0,170),xy,mask);shadow=shadow.filter(ImageFilter.GaussianBlur(24));im.paste(shadow,(0,0),shadow)
    im.paste(shot,xy,shot);return shot.size
hero=backdrop(1440,990);text(hero,(64,42),'QUOTAMONITOR / WINDOWS',17,'#83bfae',True);stamp(hero)
text(hero,(64,103),'Your AI usage, at a glance.',47,'#eef8f4',True)
text(hero,(67,171),'Codex quota  /  ClaudeCN & DSH balance  /  Daily heatmaps',21,'#a0b6b4')
paste(hero,Image.open(source/'expanded.png'),1190,(125,219))
text(hero,(66,942),'A little island. Three platforms.',23,'#deece8',True);text(hero,(1008,947),'v1.5.3  +  Bridge 1.1.0',18,'#91a6aa')
hero.save(out/'overview.png')
details=backdrop(1440,840);stamp(details);text(details,(60,40),'SMALL DETAILS, LESS DISTRACTION',18,'#83bfae',True)
text(details,(60,109),'A quiet nudge.',38,'#eef8f4',True);text(details,(62,169),'Low quota & balance alerts',21)
paste(details,Image.open(source/'alerts.png'),490,(62,241))
text(details,(60,466),'Make it yours.',38,'#eef8f4',True);text(details,(62,529),'Docking, fullscreen & refresh settings',21)
text(details,(62,700),'Original app views. Invented account data.',18,'#91a6aa')
# Crop only the genuine General settings panel, omitting unused whitespace/old static footer.
settings=Image.open(source/'settings.png').crop((0,0,1185,710))
paste(details,settings,774,(606,113),False)
details.save(out/'details.png')
base=backdrop(1200,900);text(base,(49,31),'A LITTLE ISLAND, WHEN YOU NEED IT.',18,'#83bfae',True);stamp(base)
labels=['Minimal','Collapsed','Expanded']; frames=[]
for index,path in enumerate(sorted((source/'frames').glob('*.png'))):
    im=base.copy();active=0 if index<25 or index>=115 else 1 if index<55 else 2
    for i,label in enumerate(labels):text(im,(49+i*220,100),label,25,'#e6f6ef' if i==active else '#617977',True)
    # Fixed source origin keeps the island centered during production animation.
    shot=Image.open(path).convert('RGBA');shot=shot.resize((1080,shot.height),Image.Resampling.LANCZOS)
    im.paste(shot,(60,197),shot);text(im,(49,850),'Actual WPF morph · Synthetic demo data',18,'#a2b9b4')
    frames.append(im)
# One global palette reduces frame-to-frame color flicker; no metadata/comments.
palette=hero.resize((360,235)).quantize(colors=240)
frames=[im.quantize(palette=palette,dither=Image.Dither.NONE) for im in frames]
frames[0].save(out/'island.gif',save_all=True,append_images=frames[1:],duration=40,loop=0,optimize=True,disposal=2)
print('Public artwork:',[(p.name,p.stat().st_size) for p in out.iterdir() if p.suffix in ('.png','.gif')])
