use rune::{Color, El, Shape, canvas};

fn strokes(size: f32, lines: &[&[[f32; 2]]], color: Color) -> El {
    let width = if size <= 16.0 { 1.8 } else { 2.0 };
    strokes_w(size, width, lines, color)
}

fn strokes_w(size: f32, width: f32, lines: &[&[[f32; 2]]], color: Color) -> El {
    let mut shape = Shape::new().view_box(24.0, 24.0);
    for pts in lines {
        shape = shape.stroke(pts.to_vec(), width, color);
    }
    canvas(shape).size(size, size)
}

fn arc_pts(cx: f32, cy: f32, r: f32, from: f32, to: f32, steps: usize) -> Vec<[f32; 2]> {
    (0..=steps)
        .map(|i| {
            let a = from + (to - from) * (i as f32 / steps as f32);
            [cx + r * a.cos(), cy + r * a.sin()]
        })
        .collect()
}

pub fn icon_microphone(size: f32, color: Color) -> El {
    use std::f32::consts::PI;
    // Lucide "mic": capsule body, cradle arc, stem and base.
    let mut body = arc_pts(12.0, 5.0, 3.0, PI, 2.0 * PI, 12);
    body.push([15.0, 12.0]);
    body.extend(arc_pts(12.0, 12.0, 3.0, 0.0, PI, 12));
    body.push([9.0, 5.0]);
    let mut cradle = vec![[19.0, 10.0], [19.0, 12.0]];
    cradle.extend(arc_pts(12.0, 12.0, 7.0, 0.0, PI, 16));
    cradle.push([5.0, 10.0]);
    let lines: Vec<&[[f32; 2]]> = vec![&body, &cradle, &[[12.0, 19.0], [12.0, 22.0]]];
    strokes_w(size, 1.5, &lines, color)
}

fn circle_pts(cx: f32, cy: f32, r: f32, steps: usize) -> Vec<[f32; 2]> {
    (0..=steps)
        .map(|i| {
            let a = (i as f32 / steps as f32) * std::f32::consts::TAU;
            [cx + r * a.cos(), cy + r * a.sin()]
        })
        .collect()
}

pub fn icon_grid(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.0, 4.0],
                [10.0, 4.0],
                [10.0, 10.0],
                [4.0, 10.0],
                [4.0, 4.0],
            ],
            &[
                [14.0, 4.0],
                [20.0, 4.0],
                [20.0, 10.0],
                [14.0, 10.0],
                [14.0, 4.0],
            ],
            &[
                [4.0, 14.0],
                [10.0, 14.0],
                [10.0, 20.0],
                [4.0, 20.0],
                [4.0, 14.0],
            ],
            &[
                [14.0, 14.0],
                [20.0, 14.0],
                [20.0, 20.0],
                [14.0, 20.0],
                [14.0, 14.0],
            ],
        ],
        color,
    )
}

pub fn icon_search(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(circle_pts(11.0, 11.0, 7.2, 16), 1.85, color)
        .stroke(vec![[16.3, 16.3], [21.0, 21.0]], 1.85, color);
    canvas(shape).size(size, size)
}

pub fn icon_play(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .fill(vec![[7.0, 5.0], [19.5, 12.0], [7.0, 19.0]], color);
    canvas(shape).size(size, size)
}

pub fn icon_stop(size: f32, color: Color) -> El {
    let shape = Shape::new().view_box(24.0, 24.0).fill(
        vec![[6.0, 6.0], [18.0, 6.0], [18.0, 18.0], [6.0, 18.0]],
        color,
    );
    canvas(shape).size(size, size)
}

pub fn icon_refresh(size: f32, color: Color) -> El {
    let arc: Vec<[f32; 2]> = (0..=14)
        .map(|i| {
            let a = -0.35 + (i as f32 / 14.0) * 5.0;
            [12.0 + 7.5 * a.cos(), 12.0 + 7.5 * a.sin()]
        })
        .collect();
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(arc, 1.85, color)
        .stroke(vec![[16.5, 4.5], [19.5, 9.0], [14.5, 9.5]], 1.85, color);
    canvas(shape).size(size, size)
}

pub fn icon_store(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [5.0, 8.0],
                [19.0, 8.0],
                [18.0, 20.0],
                [6.0, 20.0],
                [5.0, 8.0],
            ],
            &[
                [9.0, 8.0],
                [9.0, 5.5],
                [12.0, 4.0],
                [15.0, 5.5],
                [15.0, 8.0],
            ],
        ],
        color,
    )
}

pub fn icon_chat(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [4.5, 6.0],
            [19.5, 6.0],
            [19.5, 16.5],
            [10.0, 16.5],
            [5.5, 20.0],
            [5.5, 16.5],
            [4.5, 16.5],
            [4.5, 6.0],
        ]],
        color,
    )
}

pub fn icon_music(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(
            vec![[9.5, 17.0], [9.5, 6.0], [19.5, 4.5], [19.5, 15.0]],
            1.85,
            color,
        )
        .stroke(circle_pts(7.5, 17.5, 2.4, 12), 1.85, color)
        .stroke(circle_pts(17.5, 15.5, 2.4, 12), 1.85, color);
    canvas(shape).size(size, size)
}

pub fn icon_cpu(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [6.5, 6.5],
                [17.5, 6.5],
                [17.5, 17.5],
                [6.5, 17.5],
                [6.5, 6.5],
            ],
            &[
                [10.0, 10.0],
                [14.0, 10.0],
                [14.0, 14.0],
                [10.0, 14.0],
                [10.0, 10.0],
            ],
            &[[9.5, 3.5], [9.5, 6.5]],
            &[[14.5, 3.5], [14.5, 6.5]],
            &[[9.5, 17.5], [9.5, 20.5]],
            &[[14.5, 17.5], [14.5, 20.5]],
            &[[3.5, 9.5], [6.5, 9.5]],
            &[[3.5, 14.5], [6.5, 14.5]],
            &[[17.5, 9.5], [20.5, 9.5]],
            &[[17.5, 14.5], [20.5, 14.5]],
        ],
        color,
    )
}

pub fn icon_settings(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(circle_pts(12.0, 12.0, 3.2, 12), 1.85, color)
        .stroke(circle_pts(12.0, 12.0, 7.6, 16), 1.85, color);
    canvas(shape).size(size, size)
}

pub fn icon_close(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[[6.0, 6.0], [18.0, 18.0]], &[[18.0, 6.0], [6.0, 18.0]]],
        color,
    )
}

pub fn icon_back(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[19.0, 12.0], [5.0, 12.0]],
            &[[11.0, 6.0], [5.0, 12.0], [11.0, 18.0]],
        ],
        color,
    )
}

pub fn icon_folder(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [3.5, 6.5],
            [9.5, 6.5],
            [11.5, 9.0],
            [20.5, 9.0],
            [20.5, 18.5],
            [3.5, 18.5],
            [3.5, 6.5],
        ]],
        color,
    )
}

pub fn icon_key(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(circle_pts(8.0, 15.0, 4.0, 14), 1.85, color)
        .stroke(vec![[11.0, 12.0], [20.0, 4.5]], 1.85, color)
        .stroke(vec![[17.0, 7.0], [19.5, 9.5]], 1.85, color);
    canvas(shape).size(size, size)
}

pub fn icon_download(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[12.0, 4.0], [12.0, 15.0]],
            &[[7.5, 10.5], [12.0, 15.0], [16.5, 10.5]],
            &[[5.0, 19.0], [19.0, 19.0]],
        ],
        color,
    )
}

pub fn icon_wrench(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[6.0, 18.0], [15.0, 9.0]],
            &[[14.0, 5.0], [18.5, 5.5], [19.0, 10.0], [15.0, 9.0]],
        ],
        color,
    )
}

pub fn icon_trash(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[4.5, 6.5], [19.5, 6.5]],
            &[[9.0, 4.0], [15.0, 4.0]],
            &[[6.5, 6.5], [7.5, 19.5], [16.5, 19.5], [17.5, 6.5]],
        ],
        color,
    )
}

pub fn icon_sliders(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[4.0, 7.0], [20.0, 7.0]],
            &[[4.0, 12.0], [20.0, 12.0]],
            &[[4.0, 17.0], [20.0, 17.0]],
            &[[9.0, 4.8], [9.0, 9.2]],
            &[[15.0, 9.8], [15.0, 14.2]],
            &[[11.0, 14.8], [11.0, 19.2]],
        ],
        color,
    )
}

pub fn icon_external(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[14.0, 5.0], [19.0, 5.0], [19.0, 10.0]],
            &[[10.5, 13.5], [19.0, 5.0]],
            &[
                [17.5, 13.5],
                [17.5, 19.0],
                [5.0, 19.0],
                [5.0, 6.5],
                [10.5, 6.5],
            ],
        ],
        color,
    )
}

pub fn icon_sparkles(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[12.0, 3.5], [12.0, 20.5]],
            &[[3.5, 12.0], [20.5, 12.0]],
            &[[7.0, 7.0], [17.0, 17.0]],
            &[[17.0, 7.0], [7.0, 17.0]],
        ],
        color,
    )
}

pub fn icon_gamepad(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [6.0, 8.0],
                [18.0, 8.0],
                [20.5, 15.5],
                [17.0, 17.5],
                [14.5, 14.5],
                [9.5, 14.5],
                [7.0, 17.5],
                [3.5, 15.5],
                [6.0, 8.0],
            ],
            &[[8.5, 10.5], [8.5, 13.0]],
            &[[7.2, 11.75], [9.8, 11.75]],
        ],
        color,
    )
}

pub fn icon_trophy(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [7.0, 5.0],
                [17.0, 5.0],
                [16.0, 12.5],
                [12.0, 14.5],
                [8.0, 12.5],
                [7.0, 5.0],
            ],
            &[[12.0, 14.5], [12.0, 19.0]],
            &[[8.5, 19.0], [15.5, 19.0]],
        ],
        color,
    )
}

pub fn icon_shield(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [12.0, 3.5],
            [19.0, 6.5],
            [18.5, 13.5],
            [12.0, 20.5],
            [5.5, 13.5],
            [5.0, 6.5],
            [12.0, 3.5],
        ]],
        color,
    )
}

pub fn icon_hard_drive(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.0, 14.0],
                [20.0, 14.0],
                [20.0, 19.0],
                [4.0, 19.0],
                [4.0, 14.0],
            ],
            &[[4.0, 14.0], [6.5, 6.0], [17.5, 6.0], [20.0, 14.0]],
            &[[7.5, 16.5], [8.5, 16.5]],
            &[[11.0, 16.5], [12.0, 16.5]],
        ],
        color,
    )
}

pub fn icon_keyboard(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [3.0, 7.0],
                [21.0, 7.0],
                [21.0, 17.0],
                [3.0, 17.0],
                [3.0, 7.0],
            ],
            &[[6.5, 10.5], [7.5, 10.5]],
            &[[10.0, 10.5], [11.0, 10.5]],
            &[[13.5, 10.5], [14.5, 10.5]],
            &[[17.0, 10.5], [17.5, 10.5]],
            &[[8.0, 14.0], [16.0, 14.0]],
        ],
        color,
    )
}

pub fn icon_volume(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.5, 9.5],
                [8.5, 9.5],
                [13.0, 5.5],
                [13.0, 18.5],
                [8.5, 14.5],
                [4.5, 14.5],
                [4.5, 9.5],
            ],
            &[[16.5, 9.0], [18.0, 12.0], [16.5, 15.0]],
            &[[19.0, 6.5], [21.0, 12.0], [19.0, 17.5]],
        ],
        color,
    )
}

pub fn icon_volume_off(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.5, 9.5],
                [8.5, 9.5],
                [13.0, 5.5],
                [13.0, 18.5],
                [8.5, 14.5],
                [4.5, 14.5],
                [4.5, 9.5],
            ],
            &[[16.0, 9.0], [20.5, 15.0]],
            &[[20.5, 9.0], [16.0, 15.0]],
        ],
        color,
    )
}

pub fn icon_zap(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [13.0, 3.0],
            [5.5, 13.0],
            [12.0, 13.0],
            [11.0, 21.0],
            [18.5, 11.0],
            [12.0, 11.0],
            [13.0, 3.0],
        ]],
        color,
    )
}

pub fn icon_monitor(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [3.5, 5.0],
                [20.5, 5.0],
                [20.5, 16.0],
                [3.5, 16.0],
                [3.5, 5.0],
            ],
            &[[12.0, 16.0], [12.0, 19.5]],
            &[[8.0, 19.5], [16.0, 19.5]],
        ],
        color,
    )
}

pub fn icon_check(size: f32, color: Color) -> El {
    strokes(size, &[&[[5.0, 12.5], [10.0, 17.5], [19.0, 7.0]]], color)
}

pub fn icon_user(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [12.0, 5.0],
                [14.8, 6.2],
                [15.5, 9.0],
                [14.0, 11.5],
                [12.0, 12.0],
                [10.0, 11.5],
                [8.5, 9.0],
                [9.2, 6.2],
                [12.0, 5.0],
            ],
            &[
                [5.5, 19.5],
                [7.5, 15.8],
                [12.0, 14.5],
                [16.5, 15.8],
                [18.5, 19.5],
            ],
        ],
        color,
    )
}

pub fn icon_app_window(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [3.5, 5.0],
                [20.5, 5.0],
                [20.5, 19.0],
                [3.5, 19.0],
                [3.5, 5.0],
            ],
            &[[3.5, 9.5], [20.5, 9.5]],
        ],
        color,
    )
}

pub fn icon_pause(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[[8.5, 5.5], [8.5, 18.5]], &[[15.5, 5.5], [15.5, 18.5]]],
        color,
    )
}

pub fn icon_skip_back(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[6.5, 5.5], [6.5, 18.5]],
            &[[18.0, 6.0], [10.0, 12.0], [18.0, 18.0], [18.0, 6.0]],
        ],
        color,
    )
}

pub fn icon_skip_forward(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[17.5, 5.5], [17.5, 18.5]],
            &[[6.0, 6.0], [14.0, 12.0], [6.0, 18.0], [6.0, 6.0]],
        ],
        color,
    )
}

pub fn icon_film(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.0, 5.0],
                [20.0, 5.0],
                [20.0, 19.0],
                [4.0, 19.0],
                [4.0, 5.0],
            ],
            &[[8.0, 5.0], [8.0, 19.0]],
            &[[16.0, 5.0], [16.0, 19.0]],
            &[[4.0, 12.0], [20.0, 12.0]],
        ],
        color,
    )
}

pub fn icon_camera(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [3.5, 8.0],
                [7.5, 8.0],
                [9.0, 5.5],
                [15.0, 5.5],
                [16.5, 8.0],
                [20.5, 8.0],
                [20.5, 18.5],
                [3.5, 18.5],
                [3.5, 8.0],
            ],
            &[
                [12.0, 10.5],
                [14.5, 11.5],
                [15.0, 13.5],
                [14.0, 15.8],
                [12.0, 16.5],
                [10.0, 15.8],
                [9.0, 13.5],
                [9.5, 11.5],
                [12.0, 10.5],
            ],
        ],
        color,
    )
}

pub fn icon_star(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [12.0, 3.5],
            [14.6, 8.8],
            [20.5, 9.6],
            [16.2, 13.8],
            [17.2, 19.6],
            [12.0, 16.8],
            [6.8, 19.6],
            [7.8, 13.8],
            [3.5, 9.6],
            [9.4, 8.8],
            [12.0, 3.5],
        ]],
        color,
    )
}

pub fn icon_scissors(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[6.5, 6.5], [18.5, 18.5]],
            &[[18.5, 5.5], [6.5, 17.5]],
            &[[5.5, 5.5], [8.0, 5.5], [8.0, 8.0], [5.5, 8.0], [5.5, 5.5]],
            &[
                [5.5, 16.0],
                [8.0, 16.0],
                [8.0, 18.5],
                [5.5, 18.5],
                [5.5, 16.0],
            ],
        ],
        color,
    )
}

pub fn icon_hash(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[4.0, 9.0], [20.0, 9.0]],
            &[[4.0, 15.0], [20.0, 15.0]],
            &[[10.0, 4.0], [8.0, 20.0]],
            &[[16.0, 4.0], [14.0, 20.0]],
        ],
        color,
    )
}

pub fn icon_mic(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [9.0, 4.0],
                [15.0, 4.0],
                [15.0, 12.0],
                [9.0, 12.0],
                [9.0, 4.0],
            ],
            &[
                [5.5, 11.0],
                [5.5, 13.0],
                [8.0, 16.0],
                [16.0, 16.0],
                [18.5, 13.0],
                [18.5, 11.0],
            ],
            &[[12.0, 16.0], [12.0, 20.5]],
            &[[8.5, 20.5], [15.5, 20.5]],
        ],
        color,
    )
}

pub fn icon_mic_off(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[9.0, 4.0], [15.0, 4.0], [15.0, 11.5]],
            &[[9.0, 8.5], [9.0, 12.0], [12.5, 12.0]],
            &[[5.5, 11.0], [5.5, 13.0], [8.0, 16.0], [15.0, 16.0]],
            &[[12.0, 16.0], [12.0, 20.5]],
            &[[8.5, 20.5], [15.5, 20.5]],
            &[[3.5, 3.5], [20.5, 20.5]],
        ],
        color,
    )
}

pub fn icon_headphones(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.0, 14.0],
                [4.0, 11.0],
                [7.0, 6.0],
                [12.0, 4.5],
                [17.0, 6.0],
                [20.0, 11.0],
                [20.0, 14.0],
            ],
            &[
                [4.0, 14.0],
                [7.5, 14.0],
                [7.5, 19.5],
                [4.0, 19.5],
                [4.0, 14.0],
            ],
            &[
                [16.5, 14.0],
                [20.0, 14.0],
                [20.0, 19.5],
                [16.5, 19.5],
                [16.5, 14.0],
            ],
        ],
        color,
    )
}

pub fn icon_headphones_off(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[4.0, 14.0], [4.0, 11.0], [6.5, 7.0]],
            &[
                [10.0, 5.0],
                [12.0, 4.5],
                [17.0, 6.0],
                [20.0, 11.0],
                [20.0, 14.0],
            ],
            &[
                [4.0, 14.0],
                [7.5, 14.0],
                [7.5, 19.5],
                [4.0, 19.5],
                [4.0, 14.0],
            ],
            &[[16.5, 17.5], [16.5, 19.5], [20.0, 19.5], [20.0, 14.0]],
            &[[3.5, 3.5], [20.5, 20.5]],
        ],
        color,
    )
}

pub fn icon_users(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [7.0, 7.0],
                [11.5, 7.0],
                [11.5, 11.5],
                [7.0, 11.5],
                [7.0, 7.0],
            ],
            &[[3.5, 19.5], [5.0, 15.5], [13.5, 15.5], [15.0, 19.5]],
            &[[15.0, 7.5], [18.5, 7.5], [18.5, 11.0], [15.0, 11.0]],
            &[[16.5, 15.5], [19.5, 15.5], [21.0, 19.5]],
        ],
        color,
    )
}

pub fn icon_user_plus(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [7.0, 6.5],
                [11.5, 6.5],
                [11.5, 11.0],
                [7.0, 11.0],
                [7.0, 6.5],
            ],
            &[[3.5, 19.5], [5.0, 15.5], [13.5, 15.5], [15.0, 19.5]],
            &[[18.0, 8.5], [18.0, 14.5]],
            &[[15.0, 11.5], [21.0, 11.5]],
        ],
        color,
    )
}

pub fn icon_phone(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [5.0, 4.5],
            [8.5, 4.5],
            [10.0, 8.5],
            [8.0, 10.5],
            [13.5, 16.0],
            [15.5, 14.0],
            [19.5, 15.5],
            [19.5, 19.0],
            [16.0, 19.5],
            [10.0, 16.5],
            [7.5, 14.0],
            [4.5, 8.0],
            [5.0, 4.5],
        ]],
        color,
    )
}

pub fn icon_phone_off(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[5.0, 4.5], [8.5, 4.5], [10.0, 8.5]],
            &[
                [15.5, 14.0],
                [19.5, 15.5],
                [19.5, 19.0],
                [16.0, 19.5],
                [10.0, 16.5],
            ],
            &[[3.5, 3.5], [20.5, 20.5]],
        ],
        color,
    )
}

pub fn icon_lock(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [5.5, 11.0],
                [18.5, 11.0],
                [18.5, 20.0],
                [5.5, 20.0],
                [5.5, 11.0],
            ],
            &[
                [8.0, 11.0],
                [8.0, 7.5],
                [10.0, 5.0],
                [14.0, 5.0],
                [16.0, 7.5],
                [16.0, 11.0],
            ],
        ],
        color,
    )
}

pub fn icon_home(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[3.5, 11.0], [12.0, 4.0], [20.5, 11.0]],
            &[[5.5, 9.5], [5.5, 20.0], [18.5, 20.0], [18.5, 9.5]],
            &[[9.5, 20.0], [9.5, 14.0], [14.5, 14.0], [14.5, 20.0]],
        ],
        color,
    )
}

pub fn icon_server(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.0, 4.5],
                [20.0, 4.5],
                [20.0, 10.5],
                [4.0, 10.5],
                [4.0, 4.5],
            ],
            &[
                [4.0, 13.5],
                [20.0, 13.5],
                [20.0, 19.5],
                [4.0, 19.5],
                [4.0, 13.5],
            ],
            &[[7.5, 7.5], [8.5, 7.5]],
            &[[7.5, 16.5], [8.5, 16.5]],
        ],
        color,
    )
}

pub fn icon_bell_off(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[6.5, 10.0], [6.5, 15.5], [4.5, 17.5], [17.5, 17.5]],
            &[[10.0, 4.5], [14.0, 4.5], [17.5, 8.5], [17.5, 13.5]],
            &[[10.0, 20.0], [14.0, 20.0]],
            &[[3.5, 3.5], [20.5, 20.5]],
        ],
        color,
    )
}

pub fn icon_chevron_down(size: f32, color: Color) -> El {
    strokes(size, &[&[[6.0, 9.0], [12.0, 15.0], [18.0, 9.0]]], color)
}

pub fn icon_chevron_right(size: f32, color: Color) -> El {
    strokes(size, &[&[[9.0, 6.0], [15.0, 12.0], [9.0, 18.0]]], color)
}

pub fn icon_send(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[20.5, 4.0], [11.0, 13.5]],
            &[
                [20.5, 4.0],
                [14.0, 20.5],
                [10.5, 13.5],
                [3.5, 10.0],
                [20.5, 4.0],
            ],
        ],
        color,
    )
}

pub fn icon_reply(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[9.0, 7.0], [4.0, 12.0], [9.0, 17.0]],
            &[[4.0, 12.0], [15.5, 12.0], [19.0, 15.0], [20.0, 19.0]],
        ],
        color,
    )
}

pub fn icon_pin(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[12.0, 16.5], [12.0, 21.5]],
            &[[6.0, 16.5], [18.0, 16.5]],
            &[
                [8.5, 4.5],
                [15.5, 4.5],
                [14.5, 11.5],
                [17.5, 16.5],
                [6.5, 16.5],
                [9.5, 11.5],
                [8.5, 4.5],
            ],
        ],
        color,
    )
}

pub fn icon_smile(size: f32, color: Color) -> El {
    let ring = circle_pts(12.0, 12.0, 8.5, 24);
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(ring, 1.8, color)
        .stroke(
            vec![[8.5, 14.5], [10.5, 16.5], [13.5, 16.5], [15.5, 14.5]],
            1.8,
            color,
        )
        .stroke(vec![[9.0, 9.5], [9.2, 9.5]], 2.2, color)
        .stroke(vec![[15.0, 9.5], [15.2, 9.5]], 2.2, color);
    canvas(shape).size(size, size)
}

pub fn icon_paperclip(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [20.0, 10.5],
            [11.5, 19.0],
            [7.5, 19.0],
            [5.0, 16.5],
            [5.0, 12.5],
            [13.5, 4.0],
            [16.5, 4.0],
            [18.5, 6.0],
            [18.5, 9.0],
            [10.5, 17.0],
            [8.5, 17.0],
            [8.5, 15.0],
            [15.5, 8.0],
        ]],
        color,
    )
}

pub fn icon_edit(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [4.5, 19.5],
                [9.0, 18.5],
                [19.0, 8.5],
                [15.5, 5.0],
                [5.5, 15.0],
                [4.5, 19.5],
            ],
            &[[13.5, 7.0], [17.0, 10.5]],
        ],
        color,
    )
}

pub fn icon_plus(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[[12.0, 5.0], [12.0, 19.0]], &[[5.0, 12.0], [19.0, 12.0]]],
        color,
    )
}

pub fn icon_screen_share(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [3.5, 5.0],
                [20.5, 5.0],
                [20.5, 16.0],
                [3.5, 16.0],
                [3.5, 5.0],
            ],
            &[[8.5, 19.5], [15.5, 19.5]],
            &[[12.0, 16.0], [12.0, 19.5]],
            &[[9.0, 11.0], [12.0, 8.0], [15.0, 11.0]],
            &[[12.0, 8.0], [12.0, 13.5]],
        ],
        color,
    )
}

pub fn icon_video(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [3.5, 7.0],
                [15.0, 7.0],
                [15.0, 17.0],
                [3.5, 17.0],
                [3.5, 7.0],
            ],
            &[[15.0, 10.5], [20.5, 7.5], [20.5, 16.5], [15.0, 13.5]],
        ],
        color,
    )
}

pub fn icon_hand(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[8.0, 13.0], [8.0, 6.0]],
            &[[11.0, 12.0], [11.0, 4.5]],
            &[[14.0, 12.0], [14.0, 5.5]],
            &[[17.0, 13.0], [17.0, 7.5]],
            &[
                [6.0, 13.0],
                [6.0, 16.5],
                [9.5, 20.0],
                [15.0, 20.0],
                [18.0, 16.5],
                [18.0, 12.5],
            ],
        ],
        color,
    )
}

pub fn icon_maximize(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[4.5, 9.0], [4.5, 4.5], [9.0, 4.5]],
            &[[15.0, 4.5], [19.5, 4.5], [19.5, 9.0]],
            &[[19.5, 15.0], [19.5, 19.5], [15.0, 19.5]],
            &[[9.0, 19.5], [4.5, 19.5], [4.5, 15.0]],
        ],
        color,
    )
}

pub fn exo_mark(size: f32, color: Color) -> El {
    let slant = 1.75;
    let bar = |x: f32, y: f32, w: f32, h: f32| -> Vec<[f32; 2]> {
        vec![
            [x + slant, y],
            [x + w + slant, y],
            [x + w, y + h],
            [x, y + h],
        ]
    };
    let shape = Shape::new()
        .view_box(64.0, 64.0)
        .fill(bar(16.8, 13.0, 36.0, 9.0), color)
        .fill(bar(14.0, 27.5, 24.0, 9.0), color)
        .fill(bar(11.2, 42.0, 36.0, 9.0), color);
    canvas(shape).size(size, size)
}

pub fn icon_library(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[5.0, 5.0], [5.0, 19.0]],
            &[[10.0, 5.0], [10.0, 19.0]],
            &[[14.5, 5.5], [19.5, 18.5]],
        ],
        color,
    )
}

pub fn icon_mouse(size: f32, color: Color) -> El {
    let mut shape = Shape::new().view_box(24.0, 24.0);
    let w = if size <= 16.0 { 1.8 } else { 2.0 };
    shape = shape
        .stroke(
            vec![
                [7.0, 8.0],
                [7.5, 5.5],
                [9.5, 4.0],
                [14.5, 4.0],
                [16.5, 5.5],
                [17.0, 8.0],
                [17.0, 16.0],
                [16.5, 18.5],
                [14.5, 20.0],
                [9.5, 20.0],
                [7.5, 18.5],
                [7.0, 16.0],
                [7.0, 8.0],
            ],
            w,
            color,
        )
        .stroke(vec![[12.0, 7.5], [12.0, 11.0]], w, color);
    canvas(shape).size(size, size)
}

pub fn icon_more_horiz(size: f32, color: Color) -> El {
    let mut shape = Shape::new().view_box(24.0, 24.0);
    for cx in [6.5, 12.0, 17.5] {
        shape = shape.fill(circle_pts(cx, 12.0, 1.4, 12), color);
    }
    canvas(shape).size(size, size)
}

pub fn icon_filter(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[4.5, 7.0], [19.5, 7.0]],
            &[[7.5, 12.0], [16.5, 12.0]],
            &[[10.5, 17.0], [13.5, 17.0]],
        ],
        color,
    )
}

pub fn icon_chevron_left(size: f32, color: Color) -> El {
    strokes(size, &[&[[14.5, 6.5], [9.0, 12.0], [14.5, 17.5]]], color)
}

pub fn icon_replay(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [5.0, 11.0],
                [6.5, 7.0],
                [10.0, 5.0],
                [14.5, 5.0],
                [18.0, 7.5],
                [19.0, 12.0],
                [17.5, 16.5],
                [13.5, 19.0],
                [9.0, 18.5],
                [6.5, 16.0],
            ],
            &[[5.0, 6.0], [5.0, 11.0], [10.0, 11.0]],
        ],
        color,
    )
}

pub fn icon_record_dot(size: f32, color: Color) -> El {
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .fill(circle_pts(12.0, 12.0, 6.0, 20), color);
    canvas(shape).size(size, size)
}

pub fn icon_bookmark(size: f32, color: Color) -> El {
    strokes(
        size,
        &[&[
            [6.5, 4.5],
            [17.5, 4.5],
            [17.5, 19.5],
            [12.0, 15.5],
            [6.5, 19.5],
            [6.5, 4.5],
        ]],
        color,
    )
}

pub fn icon_minimize(size: f32, color: Color) -> El {
    strokes(size, &[&[[5.0, 12.0], [19.0, 12.0]]], color)
}

pub fn icon_win_maximize(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[15.0, 3.0], [21.0, 3.0], [21.0, 9.0]],
            &[[21.0, 3.0], [14.0, 10.0]],
            &[[3.0, 21.0], [10.0, 14.0]],
            &[[9.0, 21.0], [3.0, 21.0], [3.0, 15.0]],
        ],
        color,
    )
}

pub fn icon_win_restore(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[14.0, 10.0], [21.0, 3.0]],
            &[[20.0, 10.0], [14.0, 10.0], [14.0, 4.0]],
            &[[3.0, 21.0], [10.0, 14.0]],
            &[[4.0, 14.0], [10.0, 14.0], [10.0, 20.0]],
        ],
        color,
    )
}

pub fn icon_info(size: f32, color: Color) -> El {
    let w = if size <= 16.0 { 1.75 } else { 1.9 };
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(circle_pts(12.0, 12.0, 9.0, 24), w, color)
        .stroke(vec![[12.0, 11.2], [12.0, 16.5]], w, color)
        .fill(circle_pts(12.0, 8.0, 1.1, 10), color);
    canvas(shape).size(size, size)
}

pub fn icon_copy(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [9.0, 9.0],
                [20.0, 9.0],
                [20.0, 20.0],
                [9.0, 20.0],
                [9.0, 9.0],
            ],
            &[
                [5.0, 15.0],
                [4.0, 15.0],
                [4.0, 4.0],
                [15.0, 4.0],
                [15.0, 5.0],
            ],
        ],
        color,
    )
}

pub fn icon_link(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [10.0, 13.0],
                [14.0, 9.0],
                [16.5, 6.5],
                [18.5, 8.5],
                [16.0, 11.0],
            ],
            &[
                [14.0, 11.0],
                [10.0, 15.0],
                [7.5, 17.5],
                [5.5, 15.5],
                [8.0, 13.0],
            ],
        ],
        color,
    )
}

pub fn icon_flag(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[5.0, 21.0], [5.0, 4.0]],
            &[
                [5.0, 5.0],
                [19.0, 5.0],
                [16.5, 9.5],
                [19.0, 14.0],
                [5.0, 14.0],
            ],
        ],
        color,
    )
}

pub fn icon_forward(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[15.0, 6.0], [20.0, 11.0], [15.0, 16.0]],
            &[[20.0, 11.0], [9.0, 11.0], [5.0, 14.0], [4.0, 18.0]],
        ],
        color,
    )
}

pub fn icon_chevron_up(size: f32, color: Color) -> El {
    strokes(size, &[&[[6.5, 14.5], [12.0, 9.0], [17.5, 14.5]]], color)
}

pub fn icon_alert(size: f32, color: Color) -> El {
    let w = if size <= 16.0 { 1.75 } else { 1.9 };
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(circle_pts(12.0, 12.0, 9.0, 24), w, color)
        .stroke(vec![[12.0, 8.0], [12.0, 12.6]], w, color)
        .fill(circle_pts(12.0, 16.0, 1.1, 10), color);
    canvas(shape).size(size, size)
}

pub fn icon_bell(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [6.0, 10.0],
                [6.5, 6.5],
                [9.0, 4.5],
                [12.0, 4.0],
                [15.0, 4.5],
                [17.5, 6.5],
                [18.0, 10.0],
                [18.0, 15.0],
                [20.0, 17.5],
                [4.0, 17.5],
                [6.0, 15.0],
                [6.0, 10.0],
            ],
            &[[10.0, 20.0], [12.0, 21.0], [14.0, 20.0]],
        ],
        color,
    )
}

pub fn icon_volume_low(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [5.0, 9.5],
                [9.0, 9.5],
                [13.5, 5.5],
                [13.5, 18.5],
                [9.0, 14.5],
                [5.0, 14.5],
                [5.0, 9.5],
            ],
            &[[17.0, 9.0], [18.5, 12.0], [17.0, 15.0]],
        ],
        color,
    )
}

pub fn icon_notes(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [6.0, 3.5],
                [14.5, 3.5],
                [18.5, 7.5],
                [18.5, 20.5],
                [6.0, 20.5],
                [6.0, 3.5],
            ],
            &[[14.0, 3.5], [14.0, 8.0], [18.5, 8.0]],
            &[[9.0, 12.5], [15.5, 12.5]],
            &[[9.0, 16.5], [14.0, 16.5]],
        ],
        color,
    )
}

pub fn icon_upload(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[
                [21.0, 15.0],
                [21.0, 19.0],
                [19.0, 21.0],
                [5.0, 21.0],
                [3.0, 19.0],
                [3.0, 15.0],
            ],
            &[[17.0, 8.0], [12.0, 3.0], [7.0, 8.0]],
            &[[12.0, 3.0], [12.0, 15.0]],
        ],
        color,
    )
}

pub fn icon_arrow_right(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[5.0, 12.0], [19.0, 12.0]],
            &[[13.0, 6.0], [19.0, 12.0], [13.0, 18.0]],
        ],
        color,
    )
}

pub fn icon_rotate_ccw(size: f32, color: Color) -> El {
    let mut arc = arc_pts(
        12.0,
        12.0,
        8.5,
        -std::f32::consts::FRAC_PI_4 * 3.0,
        std::f32::consts::FRAC_PI_4 * 3.0,
        18,
    );
    arc.reverse();
    let w = if size <= 16.0 { 1.75 } else { 1.9 };
    let shape = Shape::new()
        .view_box(24.0, 24.0)
        .stroke(arc, w, color)
        .stroke(vec![[3.5, 4.0], [3.5, 9.5], [9.0, 9.5]], w, color);
    canvas(shape).size(size, size)
}

pub fn icon_bookmark_filled(size: f32, color: Color) -> El {
    let shape = Shape::new().view_box(24.0, 24.0).fill(
        vec![
            [6.5, 4.5],
            [17.5, 4.5],
            [17.5, 19.5],
            [12.0, 15.5],
            [6.5, 19.5],
        ],
        color,
    );
    canvas(shape).size(size, size)
}
