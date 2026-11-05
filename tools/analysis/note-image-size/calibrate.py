import io,json,time,zlib,math,os
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFont,ImageFilter
ROOT=Path(__file__).parent
FONT=os.environ.get('PAPERTODO_CALIBRATION_FONT', '/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf')
from sklearn.datasets import load_sample_image
photos=[Image.fromarray(load_sample_image(name)) for name in ['china.jpg','flower.jpg']]

def features(im):
 a=np.asarray(im,dtype=np.uint8); h,w,_=a.shape
 # Every pixel contributes; no sample, compressor, DCT or entropy coding here.
 out=np.zeros(19)
 for top in range(0,h,8):
  p=a[top:top+8].astype(np.int32); rows=len(p)
  d=np.empty_like(p);d[:,0]=p[:,0];d[:,1:]=(p[:,1:]-p[:,:-1]); d=d.astype(np.uint8)
  counts=np.bincount(d.ravel(),minlength=256); prob=counts[counts>0]/d.size
  ent=-(prob*np.log2(prob)).sum()*d.size/8
  rgb=d[:,:,0].astype(np.uint64)|(d[:,:,1].astype(np.uint64)<<8)|(d[:,:,2].astype(np.uint64)<<16)
  trip=len(np.unique(rgb)); pair=len(np.unique(rgb[:,1:]|(rgb[:,:-1]<<24)))
  # H0, triplet/pair diversity, changes; additive over the actual PNG window scale.
  nz=counts[1:].sum(); z=counts[0]; mask=0 if nz==0 or z==0 else -(nz*np.log2(nz/d.size)+z*np.log2(z/d.size))/8
  out[12:16]+=[ent-mask, mask, pair*ent/d.size, trip*ent/d.size]
  out[:6]+=[p.shape[0]*w,ent,trip,pair,np.count_nonzero(rgb),np.count_nonzero((d[:,:,0]!=d[:,:,1])|(d[:,:,1]!=d[:,:,2]))]
  # Local spatial complexity, accumulated over ALL 8x8 blocks.
  if rows<8: p=np.pad(p,((0,8-rows),(0,0),(0,0)),mode='edge')
  if w%8:p=np.pad(p,((0,0),(0,8-w%8),(0,0)),mode='edge')
  lum=(77*p[:,:,0].astype(np.int32)+150*p[:,:,1]+29*p[:,:,2])/256
  cb=p[:,:,2]-lum;cr=p[:,:,0]-lum
  for q,off in [(lum,6),(cb,9),(cr,9)]:
   blocks=q.reshape(8,-1,8).transpose(1,0,2)
   delta=(np.abs(np.diff(blocks,axis=1)).mean((1,2))+np.abs(np.diff(blocks,axis=2)).mean((1,2)))/2
   out[off]+=np.log2(1+delta).sum()*rows/8
   out[off+1]+=np.sqrt(delta).sum()*rows/8
   out[off+2]+=np.count_nonzero(delta)*rows/8
   if off==6:
    for scale,idx in [(2,16),(4,17)]:
     low=blocks.reshape(-1,8//scale,scale,8//scale,scale).mean((2,4))
     diff=(np.abs(np.diff(low,axis=1)).mean((1,2))+np.abs(np.diff(low,axis=2)).mean((1,2)))/2
     out[idx]+=np.log2(1+diff).sum()*rows/8
    wht=np.abs(blocks[:,0::2,0::2]-blocks[:,0::2,1::2]-blocks[:,1::2,0::2]+blocks[:,1::2,1::2]).mean((1,2))/4
    out[18]+=np.log2(1+wht).sum()*rows/8
 return out

def size(im):
 a=np.asarray(im);d=np.empty_like(a);d[:,0]=a[:,0];d[:,1:]=a[:,1:]-a[:,:-1]
 rows=np.zeros((im.height,im.width*3+1),np.uint8);rows[:,0]=1;rows[:,1:]=d.reshape(im.height,-1)
 png=len(zlib.compress(rows.tobytes(),6))+70
 b=io.BytesIO();im.save(b,format='JPEG',quality=90,subsampling=2)
 return [len(b.getvalue()),png]

def make(seed,kind,h=1200):
 rng=np.random.default_rng(seed);w=1200
 im=Image.new('RGB',(w,h),(252,250,245));d=ImageDraw.Draw(im)
 if kind in ('text','code','mixed'):
  size=int(rng.integers(24,55));font=ImageFont.truetype(FONT,size)
  chars='ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz 0123456789,.;{}[]()'
  for y in range(100,h-60,int(size*rng.uniform(1.3,2.0))):
   n=int(rng.integers(10,40)) if kind=='code' else int(1000/(size*.65))
   txt=''.join(rng.choice(list(chars),n))
   if kind=='code':d.rectangle((75,y-2,1125,y+size+3),fill=(240,236,228))
   d.text((96,y),txt,fill=(48,45,41),font=font)
 if kind=='flat':d.rectangle((96,96,1104,h-96),fill=tuple(rng.integers(0,256,3)))
 if kind in ('photo','mixed'):
  ph=photos[seed%2].resize((1008,min(h-200,700)))
  im.paste(ph,(96,100 if kind=='photo' else h//3))
 if kind=='noise':
  im=Image.fromarray(rng.integers(0,256,(h,w,3),dtype=np.uint8))
 if kind in ('texture','gradient'):
  yy,xx=np.mgrid[:h,:w];arr=np.stack([(xx*.13+yy*.02)%256,(xx*.08+yy*.09)%256,(xx*.02+yy*.08)%256],axis=-1)
  if kind=='texture': arr+=rng.normal(0,rng.uniform(2,40),arr.shape)
  im=Image.fromarray(np.clip(arr,0,255).astype('uint8'))
 if kind=='shapes':
  for i in range(150):
   x,y=rng.integers(0,w),rng.integers(0,h);s=int(rng.integers(8,200))
   d.ellipse((x,y,x+s,y+s),fill=tuple(rng.integers(0,256,3)))
 if kind=='check':
  yy,xx=np.mgrid[:h,:w];s=int(rng.integers(1,32));a=((xx//s+yy//s)%2*220+20).astype(np.uint8);im=Image.fromarray(np.stack([a,a,a],-1))
 return im

def main():
 # This tool encodes calibration labels offline. The product predictor never runs it.
 kinds=['text','code','mixed','photo','flat','noise','texture','gradient','shapes','check']
 records=[]
 for i in range(140):
  kind=kinds[i%len(kinds)];seed=i//10+13
  im=make(seed,kind); t=time.perf_counter();f=features(im);dt=time.perf_counter()-t
  records.append(dict(seed=seed,kind=kind,x=f.tolist(),y=size(im),time=dt))
  if i%20==0: print(i,flush=True)
 (ROOT/'dataset.json').write_text(json.dumps(records))
 fit(records)

def fit(records):
 x=np.array([r['x'] for r in records]);y=np.array([r['y'] for r in records]);train=np.array([r['seed']%3!=0 for r in records]);
 coeffs=[]
 for j,inds in enumerate([[0,6,7,8,9,10,11,16,17,18],[0,2,3,4,5,12,13,14,15]]):
  scale=np.maximum(x[train][:,inds].mean(0),1);xx=x[:,inds]/scale
  coef=np.linalg.lstsq(xx[train],y[train,j],rcond=None)[0]/scale
  full=np.zeros(19);full[inds]=coef; coeffs.append(full.tolist()); pred=x@full
  print('JPEG' if j==0 else 'PNG',full.tolist())
  for kind in sorted(set(r['kind'] for r in records)):
   ix=np.array([r['kind']==kind for r in records])&~train
   print(kind,'actual kB',np.round(y[ix,j]/1000).tolist(),'error kB',np.round((pred[ix]-y[ix,j])/1000).tolist())
 (ROOT/'coefficients.json').write_text(json.dumps(coeffs))
if __name__=='__main__':main()
