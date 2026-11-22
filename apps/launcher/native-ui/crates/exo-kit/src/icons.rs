use rune::{Color, El, Shape, canvas};

fn strokes(size: f32, lines: &[&[[f32; 2]]], color: Color) -> El {
    let width = if size <= 16.0 { 1.8 } else { 2.0 };
    let mut shape = Shape::new().view_box(24.0, 24.0);
    for pts in lines {
        shape = shape.stroke(pts.to_vec(), width, color);
    }
    canvas(shape).size(size, size)
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
            &[[4.0, 4.0], [10.0, 4.0], [10.0, 10.0], [4.0, 10.0], [4.0, 4.0]],
            &[[14.0, 4.0], [20.0, 4.0], [20.0, 10.0], [14.0, 10.0], [14.0, 4.0]],
            &[[4.0, 14.0], [10.0, 14.0], [10.0, 20.0], [4.0, 20.0], [4.0, 14.0]],
            &[[14.0, 14.0], [20.0, 14.0], [20.0, 20.0], [14.0, 20.0], [14.0, 14.0]],
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
            &[[5.0, 8.0], [19.0, 8.0], [18.0, 20.0], [6.0, 20.0], [5.0, 8.0]],
            &[[9.0, 8.0], [9.0, 5.5], [12.0, 4.0], [15.0, 5.5], [15.0, 8.0]],
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
        .stroke(vec![[9.5, 17.0], [9.5, 6.0], [19.5, 4.5], [19.5, 15.0]], 1.85, color)
        .stroke(circle_pts(7.5, 17.5, 2.4, 12), 1.85, color)
        .stroke(circle_pts(17.5, 15.5, 2.4, 12), 1.85, color);
    canvas(shape).size(size, size)
}

pub fn icon_cpu(size: f32, color: Color) -> El {
    strokes(
        size,
        &[
            &[[6.5, 6.5], [17.5, 6.5], [17.5, 17.5], [6.5, 17.5], [6.5, 6.5]],
            &[[10.0, 10.0], [14.0, 10.0], [14.0, 14.0], [10.0, 14.0], [10.0, 10.0]],
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
