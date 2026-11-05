"""Offline JPEG header calibration; never imported by PaperTodo.

python jpeg_calibrate.py [--data cached-corpus.json] [--photo-dir source-jpegs]
The checked-in independent validation file contains numeric measurements, not images.
"""
import argparse, io, json, math
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont
from sklearn.datasets import load_sample_image
from sklearn.linear_model import Ridge
from calibrate import labels
ROOT = Path(__file__).parent

def header(data):
 p=2; tables={}; tq=0; sampling=1
 while p+4<=len(data):
  if data[p]!=255: break
  p+=1
  while data[p]==255:p+=1
  marker=data[p];p+=1
  if marker==0xda: return len(data)-p,tables.get(tq,0),sampling
  n=int.from_bytes(data[p:p+2],'big'); seg=data[p+2:p+n]
  if marker==0xdb:
   k=0
   while k<len(seg):
    prec=seg[k]>>4; ident=seg[k]&15;k+=1
    v=np.frombuffer(seg[k:k+64*(prec+1)],dtype='>u2' if prec else 'u1');tables[ident]=float(v.mean());k+=64*(prec+1)
  if marker in (0xc0,0xc1,0xc2):
   tq=seg[8]; samp=seg[7]; sampling=(samp>>4)*(samp&15)
  p+=n
 return len(data),0,1

def make(seed,kind):
 r=np.random.default_rng(seed); w,h=768,512; y,x=np.mgrid[:h,:w]; t=seed%11
 if kind in ('china','flower'):return Image.fromarray(load_sample_image(kind+'.jpg')).resize((w,h))
 if kind in ('gradient','texture','noise','flat','plasma'):
  if kind=='flat':a=np.full((h,w,3),60+t*12)
  elif kind=='noise':a=r.integers(0,256,(h,w,3))
  else:
   a=np.stack([115+45*np.sin(x/(40+t*8)+c+.1*t)+40*np.cos(y/(50+t*9)-c) for c in range(3)],-1)
   if kind=='texture':a+=r.integers(-(4+t*4),5+t*4,a.shape)
   if kind=='plasma':
    for size,amp in [(16,15),(64,8),(256,4)]:
     small=Image.fromarray(r.integers(0,256,(size,size,3),dtype=np.uint8)).resize((w,h),Image.Resampling.BILINEAR)
     a+=(np.asarray(small).astype(float)-128)*amp/40
  return Image.fromarray(np.clip(a,0,255).astype(np.uint8))
 im=Image.new('RGB',(w,h),(252,250,245)); d=ImageDraw.Draw(im)
 if kind=='screenshot':
  f=ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',18+t)
  for yy in range(10,h-30,28+t):
   text=''.join(r.choice(list('Hello world 0123456789 ABCxyz={}[]'),40))
   d.text((10,yy),text,font=f,fill=(30,45,65))
 else:
  for i in range(30+t*10):
   xx,yy=r.integers(0,w),r.integers(0,h); sz=r.integers(10,150)
   d.rectangle((xx,yy,xx+sz,yy+sz),fill=tuple(r.integers(0,256,3)))
 return im


def measure(name, im, split):
    records = []
    for quality in (55, 70, 82, 90, 95, 98):
        for subsampling in (0, 2):
            encoded = io.BytesIO()
            im.save(encoded, format='JPEG', quality=quality, subsampling=subsampling)
            data = encoded.getvalue()
            payload, quant, sampling = header(data)
            source = Image.open(io.BytesIO(data)).convert('RGB')
            for scale in (.25, .5, 1., 1.5):
                output = source.resize((round(im.width*scale), round(im.height*scale)), Image.Resampling.BILINEAR)
                records.append(dict(name=name, split=split, q=quality, sub=subsampling,
                    quant=quant, sampling=sampling, density=len(data)/(im.width*im.height),
                    payload=payload/(im.width*im.height), scale=scale,
                    area=output.width*output.height, y=labels(output)))
    return records


def dataset():
    records = []
    for seed in (17, 23, 37):
        kinds = ('flat','gradient','texture','noise','plasma','screenshot','shapes')
        if seed == 17:
            kinds += ('china', 'flower')
        for kind in kinds:
            records.extend(measure(f'{kind}-{seed}', make(seed, kind), 'validation' if seed == 37 else 'train'))
    ROOT.joinpath('jpeg-corpus.json').write_text(json.dumps(records))
    return records

def features(rows,version=1):
 d=np.log([r['payload'] for r in rows]);s=np.log([r['scale'] for r in rows]);q=np.log([r['quant'] for r in rows]);h=np.log([r['sampling'] for r in rows]);
 f=[np.ones_like(d),d,d*d,s,s*s,d*s,q,q*q,d*q,s*q,h,d*h,s*h]
 if version>=2:f += [d*d*d,d*d*s,d*d*q,d*q*q,d*s*q,s*s*q]
 return np.column_stack(f)

def baseline(rows,target):
 d=np.log([r['density'] for r in rows]);s=np.log([r['scale'] for r in rows]);a=np.array([r['area'] for r in rows])
 if target==0:
  rate=np.exp(-.835228+.396450*d-.098479*d*d+.030032*s+.178976*d*s)
  rate=np.where(d>math.log(.5),np.maximum(rate,.85*np.exp(d)*np.minimum(1,np.exp(s))),rate)
 else:
  rate=np.exp(.609807-.381840*d-.418132*d*d+.039313*s+.162505*d*s)
  rate=np.where(d>math.log(.025),np.maximum(rate,np.minimum(.4,8*np.exp(d))),rate)
 return np.clip(rate,.015 if target==0 else .001,2 if target==0 else 3)*a

def report(rows,pred,target):
 actual=np.array([r['y'][target] for r in rows]);e=np.abs(pred/actual-1)
 return dict(n=len(e),median=round(float(np.median(e)),4),p90=round(float(np.quantile(e,.9)),4),within50=round(float(np.mean(e<=.5)),4),logMAE=round(float(np.mean(abs(np.log(pred/actual)))),4))


def predict(rows, coefficients, target):
    area = np.array([r['area'] for r in rows])
    prediction = np.clip(np.exp(features(rows, 2) @ coefficients),
        .015 if target == 0 else .001, 2 if target == 0 else 3) * area
    usable = np.array([2 <= r['quant'] <= 60 and 1 <= r['sampling'] <= 4 for r in rows])
    if target == 1:
        usable &= np.array([r['payload'] > 1.5*(.01171875+.015625/r['sampling']) for r in rows])
    # Runtime removes APP/ICC/header bytes even when using the old coarse fallback.
    fallback_rows = [dict(r, density=r['payload']) for r in rows]
    return np.where(usable, prediction, baseline(fallback_rows, target))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', type=Path)
    parser.add_argument('--photo-dir', type=Path)
    args = parser.parse_args()
    rows = json.loads(args.data.read_text()) if args.data else dataset()
    train = [r for r in rows if r['split'] == 'train' and r['name'] not in ('china-23','flower-23')]
    validation = [r for r in rows if r['split'] == 'validation' and not r['name'].startswith(('china','flower'))]
    independent = json.loads(ROOT.joinpath('jpeg-validation.json').read_text())['records']
    if args.photo_dir:
        independent = []
        for name in ('baboon','building','fruits','hopper'):
            independent.extend(measure(name, Image.open(args.photo_dir/(name+'.jpg')).convert('RGB'), 'test'))
    for target, name in enumerate(('JPG','PNG')):
        actual = np.array([r['y'][target] for r in train])
        area = np.array([r['area'] for r in train])
        weights = None if target == 0 else np.minimum(actual/524288, 1)
        coefficients = Ridge(alpha=.1, fit_intercept=False).fit(
            features(train, 2), np.log(actual/area), sample_weight=weights).coef_
        print(name, 'coefficients', coefficients.tolist())
        for split, group in (('validation', validation), ('independent photos', independent)):
            print(name, split, 'before', report(group, baseline(group, target), target),
                  'after', report(group, predict(group, coefficients, target), target))


if __name__ == '__main__':
    main()
