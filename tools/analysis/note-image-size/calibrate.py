import io, json, math, zlib
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from sklearn.datasets import load_sample_image

def labels(im):
    a=np.asarray(im.convert('RGB')); d=np.empty_like(a); d[:,0]=a[:,0]; d[:,1:]=a[:,1:]-a[:,:-1]
    rows=np.zeros((im.height,im.width*3+1),np.uint8); rows[:,0]=1; rows[:,1:]=d.reshape(im.height,-1)
    b=io.BytesIO(); im.save(b,format='JPEG',quality=90,subsampling=2)
    return len(b.getvalue()),len(zlib.compress(rows.tobytes(),6))+70

def picture(seed,kind):
    r=np.random.default_rng(seed); w,h=1008,640; yy,xx=np.mgrid[:h,:w]
    if kind in ('flat','gradient','texture','noise'):
        if kind=='flat': a=np.full((h,w,3),180,dtype=np.uint8)
        elif kind=='noise': a=r.integers(0,256,(h,w,3),dtype=np.uint8)
        else:
            a=np.stack([100+50*np.sin(xx/75.+c)+40*np.cos(yy/92.-c) for c in range(3)],-1)
            if kind=='texture': a+=r.integers(-14,15,a.shape)
            a=np.clip(a,0,255).astype(np.uint8)
        return Image.fromarray(a)
    if kind in ('china','flower'):
        return Image.fromarray(load_sample_image(kind+'.jpg')).resize((w,h))
    im=Image.new('RGB',(w,h),(252,250,245)); d=ImageDraw.Draw(im)
    if kind=='screenshot':
        f=ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',26)
        for y in range(20,h-30,38):
            s=''.join(r.choice(list('Hello world 1234567890 abcdef XYZ {}:;'),50))
            d.text((20,y),s,font=f,fill=(30,45,65))
    else:
        for _ in range(100):
            x,y=r.integers(0,w),r.integers(0,h); sz=r.integers(10,120)
            d.rectangle((x,y,x+sz,y+sz),fill=tuple(r.integers(0,256,3)))
    return im

def main():
    records=[]
    for seed in (17,23,37):
        for kind in ('flat','gradient','texture','noise','china','flower','screenshot','shapes'):
            im=picture(seed,kind)
            for fmt,q in [('PNG',0),('JPEG',82),('JPEG',90),('JPEG',98)]:
                b=io.BytesIO(); im.save(b,format=fmt,**({'quality':q,'subsampling':2} if q else {}))
                data=b.getvalue(); src=Image.open(io.BytesIO(data)).convert('RGB')
                for scale in (.25,.5,1.,1.5):
                    out=src.resize((round(im.width*scale),round(im.height*scale)),Image.Resampling.BILINEAR)
                    y=labels(out); density=len(data)/(im.width*im.height)
                    records.append(dict(seed=seed,kind=kind,fmt=fmt,q=q,density=density,scale=scale,area=out.width*out.height,y=y))
    Path(__file__).with_name('metadata-images.json').write_text(json.dumps(records))
    for fmt in ('PNG','JPEG'):
        rr=[r for r in records if r['fmt']==fmt]; a=np.array([r['area'] for r in rr]); train=np.array([r['seed']!=37 for r in rr]);
        x=np.array([[1,math.log(r['density']),math.log(r['density'])**2,math.log(r['scale']),math.log(r['density'])*math.log(r['scale'])] for r in rr])
        for j,name in enumerate(('jpg','png')):
            y=np.array([r['y'][j] for r in rr]); c=np.linalg.lstsq(x[train],np.log(y[train]/a[train]),rcond=None)[0]; rate=np.exp(x@c)
            if fmt=='PNG' and j==0:
                rate=np.array([max(v,.8/r['scale']) if r['density']>2.8 and r['scale']>=1 else v for v,r in zip(rate,rr)])
            pred=np.clip(rate,.015 if j==0 else .001,2 if j==0 else 3)*a; err=np.abs(pred/y-1)
            print(fmt,name,c.tolist(),'within50',np.mean(err[~train]<=.5),'median',np.median(err[~train]),'max',err[~train].max())
            for kind in ('flat','gradient','texture','noise','china','flower','screenshot','shapes'):
                ix=[i for i,r in enumerate(rr) if r['kind']==kind and r['seed']==37 and r['scale']==1 and r['q'] in (0,90)]
                print(kind,[(round(rr[i]['density'],3),round(y[i]/a[i],3),round(pred[i]/a[i],3)) for i in ix])

if __name__=='__main__':main()
