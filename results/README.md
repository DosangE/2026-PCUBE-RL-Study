# Training results

`camera_run_07` is the completed Week 3 FoodCollector camera-sensor run.

| Item | Value |
|---|---:|
| Final checkpoint | 3,000,456 steps |
| Training time | 7,898.5 s (2 h 11 m 38 s) |
| Final mean reward | 147.33 |
| TensorBoard smoothed reward (0.6) | 146.18 |
| Parallel environments | 9 |

The directory contains the final ONNX model, the resumable `checkpoint.pt`, the
five retained checkpoints, TensorBoard events, and ML-Agents run metadata.

Open the curve on any machine after cloning the repository:

```bash
tensorboard --logdir results/camera_run_07
```

To resume, raise `max_steps` in `week3/FoodCollector/config/foodCollector.yaml`,
then run ML-Agents with `--run-id=camera_run_07 --resume`.
