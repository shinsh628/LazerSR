import math
def logistic(x, mid, mult): return 1/(1+math.exp(mult*(mid-x)))
def db(a,b,eps=1): return a-b>eps  # Precision.DefinitelyBigger(a,b,1)

def sr(notes, cols=4):
    notes = sorted(notes, key=lambda n:(round(n[0]), n[2]))
    objs=[]; prevs=[None]*cols; lastcol=[None]*cols; colprev=[None]*cols
    per=[]
    for i in range(1,len(notes)):
        s,e,c = notes[i]; ps = notes[i-1][0]
        pa = list(per[-1]['prev']) if per else [None]*cols
        if per: pa[per[-1]['c']] = per[-1]
        o = dict(s=s,e=e,c=c,dt=s-ps,prev=pa)
        cp = colprev[c]
        o['cst'] = (s-cp['s']) if cp else s
        colprev[c]=o
        per.append(o)
    ind=[0.0]*cols; hi=0.0; ov=1.0; cur=0.0
    peaks=[]; peak=0.0; secend=None
    def dec(v,t,b): return v*b**(t/1000)
    for k,o in enumerate(per):
        if k==0: secend=math.ceil(o['s']/400)*400
        while o['s']>secend:
            peaks.append(peak)
            pv=per[k-1]['s']
            peak = dec(hi,secend-pv,0.125)+dec(ov,secend-pv,0.30)
            secend+=400
        s,e=o['s'],o['e']
        hf_i=1.0
        for p in o['prev']:
            if p and db(p['e'],e) and db(s,p['s']): hf_i=1.25;break
        ind[o['c']]=dec(ind[o['c']],o['cst'],0.125)+2.0*hf_i
        hi = max(hi,ind[o['c']]) if o['dt']<=1 else ind[o['c']]
        ov=dec(ov,o['dt'],0.30)
        ovl=False; hf=1.0; closest=abs(e-s)
        for p in o['prev']:
            if not p: continue
            ovl |= db(p['e'],s) and db(e,p['e']) and db(s,p['s'])
            if db(p['e'],e) and db(s,p['s']): hf=1.25
            closest=min(closest,abs(e-p['e']))
        add=logistic(closest,30,0.27) if ovl else 0
        ov+=(1+add)*hf
        strain=hi+ov-cur
        cur+=strain
        peak=max(peak,cur)
    peaks.append(peak)
    d=0;w=1
    for p in sorted([p for p in peaks if p>0],reverse=True):
        d+=p*w; w*=0.9
    return d*0.018

T=20000
def tap_stream(cols_used, kps_per_col):
    step=1000/(kps_per_col*len(cols_used)); n=[];t=1000;i=0
    while t<T: n.append((t,t,cols_used[i%len(cols_used)])); t+=step;i+=1
    return n
def chords(kps):
    n=[];t=1000
    while t<T:
        for c in range(4): n.append((t,t,c))
        t+=1000/kps
    return n
def ladder(cols_used,kps_per_col,L):
    step=1000/(kps_per_col*len(cols_used)); n=[];t=1000;i=0
    while t<T: n.append((t,t+L,cols_used[i%len(cols_used)])); t+=step;i+=1
    return n
def nested(inner_cols,kps_per_col,L,holdcols):
    n=ladder(inner_cols,kps_per_col,L)
    for hc in holdcols: n.append((900,T+500,hc))
    return n
r={}
r['A 4K tap stream 28kps']=sr(tap_stream([0,1,2,3],7))
r['B 4K chords 7/s (28 notes/s)']=sr(chords(7))
r['C 4K LN ladder 28kps L=100']=sr(ladder([0,1,2,3],7,100))
r['D 3col tap 21kps']=sr(tap_stream([1,2,3],7))
r['E 3col LN ladder 21kps nested in 1 long hold']=sr(nested([1,2,3],7,100,[0]))
r['F 3col LN ladder 21kps, no outer hold']=sr(ladder([1,2,3],7,100))
r['G 2col ladder 14kps nested in 2 long holds']=sr(nested([2,3],7,100,[0,1]))
r['H 2col tap 14kps']=sr(tap_stream([2,3],7))
for k,v in r.items(): print(f"{k:50s} {v:7.2f}")
