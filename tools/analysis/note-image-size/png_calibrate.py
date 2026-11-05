"""Offline PNG format-header calibration; no image processing runs in the app.

python png_calibrate.py [--data cached-png-corpus.json] [--photo-dir source-jpegs]
The four independent source photographs never enter coefficient fitting.
"""
import argparse, io, json, math
from pathlib import Path
import numpy as np
from PIL import Image
from sklearn.linear_model import Ridge
from jpeg_calibrate import make
from calibrate import labels
ROOT = Path(__file__).parent

def header(data):
 p=8; payload=0; colors=0;alpha=0;depth=0;typ=0
 while p+12<=len(data):
  n=int.from_bytes(data[p:p+4],'big');kind=data[p+4:p+8];v=data[p+8:p+8+n];p+=n+12
  if kind==b'IHDR':depth,typ=v[8],v[9];alpha=int(typ in (4,6))
  elif kind==b'IDAT':payload+=n
  elif kind==b'PLTE':colors=n//3
  elif kind==b'tRNS':alpha=1
  elif kind==b'IEND':break
 return payload,depth,typ,colors,alpha

def variant(im,mode):
 if mode=='RGB':return im,{}
 if mode=='L':return im.convert('L'),{}
 if mode=='1':return im.convert('L').point(lambda p:255 if p>130 else 0,mode='1'),{}
 if mode.startswith('P'):
  k=int(mode[1:]);return im.quantize(colors=k,method=Image.Quantize.MEDIANCUT),{'bits':int(math.log2(k))}
 if mode=='RGBA-opaque':return im.convert('RGBA'),{}
 if mode=='RGBA-fade':
  a=np.asarray(im.convert('RGBA')).copy();h,w=a.shape[:2];y,x=np.mgrid[:h,:w];a[:,:,3]=(255*np.clip(1-((x-w*.45)/(w*.55))**2-((y-h*.45)/(h*.6))**2,0,1)).astype(np.uint8)
  return Image.fromarray(a),{}
 raise ValueError(mode)

def measure(name, im, split):
    rows = []
    for mode in ('RGB', 'L', '1', 'P4', 'P16', 'P256', 'RGBA-opaque', 'RGBA-fade'):
        source, options = variant(im, mode)
        targets = []
        for scale in (.25, .5, 1., 1.5):
            output = source.convert('RGBA').resize(
                (round(im.width * scale), round(im.height * scale)), Image.Resampling.BILINEAR)
            background = Image.new('RGBA', output.size, 'white')
            background.alpha_composite(output)
            targets.append((scale, output.width * output.height, labels(background.convert('RGB'))))
        for level in (1, 6, 9):
            encoded = io.BytesIO()
            source.save(encoded, format='PNG', compress_level=level, **options)
            data = encoded.getvalue()
            payload, depth, color_type, colors, alpha = header(data)
            for scale, area, target in targets:
                rows.append(dict(name=name, split=split, mode=mode, level=level,
                    density=len(data)/(im.width*im.height), payload=payload/(im.width*im.height),
                    depth=depth, type=color_type, colors=colors, alpha=alpha, scale=scale, area=area, y=target))
    return rows


def dataset():
    rows = []
    for seed in (17, 23, 37):
        kinds = ('flat', 'gradient', 'texture', 'noise', 'plasma', 'screenshot', 'shapes')
        if seed == 17:
            kinds += ('china', 'flower')
        for kind in kinds:
            rows.extend(measure(f'{kind}-{seed}', make(seed, kind), 'validation' if seed == 37 else 'train'))
    ROOT.joinpath('png-corpus.json').write_text(json.dumps(rows))
    return rows


def features(rows,version=1):
 d=np.log([max(.001,r['payload']) for r in rows]);s=np.log([r['scale'] for r in rows]);gray=np.array([r['type'] in (0,4) for r in rows],float);pal=np.array([r['type']==3 for r in rows],float);alpha=np.array([r['alpha'] for r in rows],float);depth=np.log([r['depth']/8 for r in rows]);colors=np.log([max(1,r['colors'])/256 for r in rows])*pal
 f=[np.ones_like(d),d,d*d,s,s*s,d*s,gray,gray*d,gray*s,alpha,alpha*d,alpha*s,pal,pal*d,pal*s,colors,colors*d,depth]
 if version>=2:f += [d*d*d,d*d*s,d*s*s]
 return np.column_stack(f)

def baseline(rows,t):
 d=np.log([min(4,max(.001,r['density'])) for r in rows]);s=np.log([r['scale'] for r in rows]);a=np.array([r['area'] for r in rows])
 co=[[-2.062611,.555156,.658371,.088997,-.005942,-.176980,-.057669,.121336],[-.044796,1.046804,.022870,-.133692,-.026168,-.038347,.110734,.168795]][t]
 aa,b,c,dd,e,f,g,h=co;p=np.exp(aa+d*(b+d*(c+d*(dd+d*e)))+f*s+g*s*s+h*d*s)
 if t==0:p=np.where((d>math.log(2.8))&(s>=0),np.maximum(p,.8/np.exp(s)),p)
 return np.clip(p,.015 if t==0 else .001,2 if t==0 else 3)*a

def report(rows,pred,t):
 actual=np.array([r['y'][t] for r in rows]);err=np.abs(pred/actual-1)
 return dict(n=len(rows),median=float(np.median(err)),p90=float(np.quantile(err,.9)),within50=float(np.mean(err<=.5)),logMAE=float(np.mean(abs(np.log(pred/actual)))),budgetPass=float(np.mean(abs(pred-actual)<=np.maximum(524288,actual*.5))))

def predict(rows, coefficients, target):
    area = np.array([r['area'] for r in rows])
    candidate = np.clip(np.exp(features(rows, 2) @ coefficients),
        .015 if target == 0 else .001, 2 if target == 0 else 3) * area
    fallback = baseline([dict(r, density=r['payload']) for r in rows], target)
    usable = np.array([r['depth'] <= 8 and (r['type'] == 6 or
        (not r['alpha'] and r['type'] in (0, 3))) for r in rows])
    if target == 0:
        candidate = np.where([bool(r['alpha']) for r in rows], np.sqrt(fallback*candidate), candidate)
    return np.where(usable, candidate, fallback)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', type=Path)
    parser.add_argument('--photo-dir', type=Path)
    args = parser.parse_args()
    rows = json.loads(args.data.read_text()) if args.data else dataset()
    train = [r for r in rows if r['split'] == 'train']
    validation = [r for r in rows if r['split'] == 'validation']
    independent = json.loads(ROOT.joinpath('png-validation.json').read_text())['records']
    if args.photo_dir:
        independent = []
        for name in ('baboon', 'building', 'fruits', 'hopper'):
            independent.extend(measure(name, Image.open(args.photo_dir/(name+'.jpg')).convert('RGB'), 'test'))
    for target, name in enumerate(('JPG', 'PNG')):
        area = np.array([r['area'] for r in train])
        actual = np.array([r['y'][target] for r in train])
        coefficients = Ridge(alpha=.1, fit_intercept=False).fit(features(train, 2), np.log(actual/area)).coef_
        print(name, 'coefficients', coefficients.tolist())
        for split, group in (('validation', validation), ('independent photos', independent)):
            print(name, split, 'before', report(group, baseline(group, target), target),
                  'after', report(group, predict(group, coefficients, target), target))


if __name__ == '__main__':
    main()
